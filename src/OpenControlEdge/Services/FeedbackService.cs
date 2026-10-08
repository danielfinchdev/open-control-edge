using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OpenControlEdge.Services;

/// An image attached to the feedback: a screenshot taken from Settings or a PNG / JPEG chosen by the user.
internal sealed record FeedbackImage(string Name, byte[] Bytes);

/// Feedback from Settings > Feedback, two ways:
///  - Without any account: posted to FormSubmit (formsubmit.co), which mails it, screenshots attached, to the author's
///    feedback inbox. The endpoint is the random alias FormSubmit gives after activation, so no address is in the code.
///  - With a GitHub account: a new issue of the repository, already filled in (GitHubIssueUri).
/// Nothing is sent without the user pressing the button; the text and images are exactly what the page shows.
internal static class FeedbackService
{
    /// FormSubmit alias of the feedback inbox (https://formsubmit.co/<alias>). Empty: sending without an account
    /// is not available in this build.
    internal const string FormAlias = "1ee33dd306d01e9d63121af2b9fd1a2a";

    internal const int MaxImages = 3;
    internal const int MaxImageBytes = 5 * 1024 * 1024;
    internal const int MaxMessageLength = 5000;
    private const string Repository = "https://github.com/danielfinchdev/open-control-edge";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public static bool CanSendWithoutAccount => FormAlias.Length > 0;

    /// Sends the feedback. Null when FormSubmit accepted it, otherwise a Spanish message for the page.
    public static async Task<string?> SendAsync(string type, string message, string? contact, IReadOnlyList<FeedbackImage> images)
    {
        if (!CanSendWithoutAccount) return "El envío sin cuenta no está disponible en esta versión";
        if (string.IsNullOrWhiteSpace(message)) return "Escribe un mensaje antes de enviarlo";
        try
        {
            using var form = new MultipartFormDataContent();
            void Field(string name, string value) => form.Add(new StringContent(value), name);
            Field("_subject", $"Open Control Edge: {type}");
            Field("_template", "table");
            Field("_captcha", "false");
            // Field names in plain ASCII: the mail shows them as they are (an accent would be MIME-encoded).
            Field("Tipo", type);
            Field("Mensaje", message.Length > MaxMessageLength ? message[..MaxMessageLength] : message);
            Field("Version", typeof(FeedbackService).Assembly.GetName().Version?.ToString(3) ?? "?");
            Field("Windows", Environment.OSVersion.VersionString);
            if (!string.IsNullOrWhiteSpace(contact) && contact.Contains('@') && contact.Length <= 120)
            {
                Field("Contacto", contact.Trim());
                Field("_replyto", contact.Trim());
            }
            for (int i = 0; i < images.Count && i < MaxImages; i++)
            {
                var file = new ByteArrayContent(images[i].Bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(IsPng(images[i].Bytes) ? "image/png" : "image/jpeg");
                form.Add(file, $"captura{i + 1}", images[i].Name);
            }

            // The plain endpoint, not /ajax/: only the plain one forwards attachments.
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://formsubmit.co/" + FormAlias) { Content = form };
            request.Headers.Accept.ParseAdd("text/html");
            // FormSubmit only answers requests that come from a web page: the project's page is where the form lives.
            request.Headers.TryAddWithoutValidation("Origin", "https://github.com");
            request.Headers.Referrer = new Uri(Repository);
            request.Headers.UserAgent.ParseAdd(UsageHttp.UserAgent);
            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Feedback", $"HTTP {(int)response.StatusCode}");
                return $"Error HTTP {(int)response.StatusCode}";
            }
            // A thank-you page that says so; anything else (activation, captcha, error page) is not a delivery.
            if (body.Contains("submitted successfully", StringComparison.OrdinalIgnoreCase)) return null;
            Log.Warn("Feedback", "FormSubmit no aceptó el envío");
            return "No se pudo enviar el mensaje";
        }
        catch (TaskCanceledException) { return "Tiempo de espera agotado"; }
        catch (HttpRequestException) { return "Sin conexión"; }
    }

    /// A new issue of the repository with title and body filled in (the user signs in to GitHub and sends it).
    public static Uri GitHubIssueUri(string type, string message)
    {
        string version = typeof(FeedbackService).Assembly.GetName().Version?.ToString(3) ?? "?";
        string body = $"{(message.Length > 4000 ? message[..4000] : message)}\n\n---\nOpen Control Edge {version} · {Environment.OSVersion.VersionString}";
        return new Uri($"{Repository}/issues/new?title={Uri.EscapeDataString($"[{type}] ")}&body={Uri.EscapeDataString(body)}");
    }

    /// A PNG or JPEG file of at most MaxImageBytes, by its first bytes (not its extension); null otherwise.
    public static FeedbackImage? LoadImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxImageBytes) return null;
            byte[] bytes = File.ReadAllBytes(path);
            return IsPng(bytes) || IsJpeg(bytes) ? new FeedbackImage(Path.GetFileName(path), bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// The whole primary screen as a PNG (scaled down to at most 1920 px wide so it stays under the size limit).
    public static FeedbackImage? CaptureScreen()
    {
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            int width = GetSystemMetrics(0), height = GetSystemMetrics(1);   // SM_CXSCREEN, SM_CYSCREEN
            if (screen == IntPtr.Zero || width <= 0 || height <= 0) return null;
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, width, height);
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, 0, 0, 0x00CC0020 | 0x40000000)) return null; // SRCCOPY | CAPTUREBLT
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (source.PixelWidth > 1920)
            {
                double scale = 1920.0 / source.PixelWidth;
                source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            byte[] png = stream.ToArray();
            return png.Length <= MaxImageBytes ? new FeedbackImage($"captura-{DateTime.Now:yyyyMMdd-HHmmss}.png", png) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Feedback", "captura: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr gdiObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
