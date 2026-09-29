using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using OpenControlEdge.Services;
using OpenControlEdge.Ui;

namespace OpenControlEdge.Views;

public partial class ApiKeyWindow : Window
{
    internal ApiKeyWindow(string? focusProvider = null, bool previewMode = false)
    {
        InitializeComponent();
        DeepSeekStatus.Text = !previewMode && ProviderKeyStore.IsConfigured("deepseek") ? Loc.Get("Keys.Saved") : Loc.Get("Keys.None");
        OpenRouterStatus.Text = !previewMode && ProviderKeyStore.IsConfigured("openrouter") ? Loc.Get("Keys.Saved") : Loc.Get("Keys.None");
        Loaded += (_, _) => (focusProvider == "openrouter" ? OpenRouterKey : DeepSeekKey).Focus();
    }

    private void OnSaveDeepSeek(object sender, RoutedEventArgs e) => SaveKey("deepseek", DeepSeekKey, DeepSeekStatus);
    private void OnSaveOpenRouter(object sender, RoutedEventArgs e) => SaveKey("openrouter", OpenRouterKey, OpenRouterStatus);
    private void OnDeleteDeepSeek(object sender, RoutedEventArgs e) => DeleteKey("deepseek", DeepSeekKey, DeepSeekStatus);
    private void OnDeleteOpenRouter(object sender, RoutedEventArgs e) => DeleteKey("openrouter", OpenRouterKey, OpenRouterStatus);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void SaveKey(string provider, System.Windows.Controls.PasswordBox box, System.Windows.Controls.TextBlock status)
    {
        SecureString secure = box.SecurePassword;
        IntPtr pointer = IntPtr.Zero;
        char[] chars = Array.Empty<char>();
        byte[] bytes = Array.Empty<byte>();
        try
        {
            if (secure.Length == 0) { status.Text = Loc.Get("Keys.Empty"); return; }
            pointer = Marshal.SecureStringToGlobalAllocUnicode(secure);
            chars = new char[secure.Length];
            Marshal.Copy(pointer, chars, 0, chars.Length);
            int start = 0;
            int length = chars.Length;
            while (length > 0 && char.IsWhiteSpace(chars[start])) { start++; length--; }
            while (length > 0 && char.IsWhiteSpace(chars[start + length - 1])) length--;
            if (length == 0) { status.Text = Loc.Get("Keys.Empty"); return; }
            bytes = Encoding.UTF8.GetBytes(chars, start, length);
            status.Text = ProviderKeyStore.Save(provider, bytes) ? Loc.Get("Keys.Saved") : Loc.Get("Keys.SaveFailed");
            box.Clear();
        }
        catch { status.Text = Loc.Get("Keys.SaveFailed"); }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            if (chars.Length > 0) Array.Clear(chars);
            if (bytes.Length > 0) CryptographicOperations.ZeroMemory(bytes);
            secure.Dispose();
        }
    }

    private static void DeleteKey(string provider, System.Windows.Controls.PasswordBox box, System.Windows.Controls.TextBlock status)
    {
        status.Text = ProviderKeyStore.Delete(provider) ? Loc.Get("Keys.Deleted") : Loc.Get("Keys.DeleteFailed");
        box.Clear();
    }
}
