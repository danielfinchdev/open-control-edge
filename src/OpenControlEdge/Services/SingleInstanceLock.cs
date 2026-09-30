using System.Runtime.InteropServices;
using System.Security.Principal;
using static OpenControlEdge.Interop.SecurityNative;

namespace OpenControlEdge.Services;

/// One widget per session. Elevated, the lock lives in a private namespace whose boundary is the Administrators
/// group: a program running as the user without elevation cannot create or open that namespace, so it cannot take
/// the lock first and keep the widget from starting. The Local\ mutex is still taken, best effort, so that a copy
/// started without elevation (Debug, a copy started by hand) sees the elevated one; when somebody else already holds
/// it, the elevated widget starts anyway and logs why. Not elevated, only the Local\ mutex exists.
internal sealed class SingleInstanceLock : IDisposable
{
    private const string LocalName = @"Local\OpenControlEdge.SingleInstance.7F3C2A1E";
    private const string BoundaryName = "OpenControlEdge.Boundary";
    private const string NamespaceAlias = "OpenControlEdge.7F3C2A1E";
    private const string PrivateName = NamespaceAlias + @"\SingleInstance";

    private IntPtr _namespace;
    private Mutex? _private;
    private Mutex? _local;

    private SingleInstanceLock() { }

    /// The lock, or null when another instance holds it (the reason is logged). Never throws.
    public static SingleInstanceLock? Acquire()
    {
        var instance = new SingleInstanceLock();
        try
        {
            if (UnelevatedLauncher.IsElevated)
            {
                if (!instance.AcquirePrivate()) { instance.Dispose(); return null; }
                if (instance._local is null) instance.AcquireLocal(required: false);
                return instance;
            }
            if (!instance.AcquireLocal(required: true)) { instance.Dispose(); return null; }
            return instance;
        }
        catch (Exception ex)
        {
            Log.Warn("App", "mutex de instancia única: " + ex.GetType().Name);
            instance.Dispose();
            return null;
        }
    }

    private bool AcquirePrivate()
    {
        IntPtr boundary = CreateBoundaryDescriptorW(BoundaryName, 0);
        if (boundary == IntPtr.Zero)
        {
            Log.Warn("App", $"sin descriptor de límite (error {Marshal.GetLastWin32Error()}); se usa solo el mutex local");
            return AcquireLocal(required: true);
        }
        IntPtr sid = IntPtr.Zero;
        try
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            byte[] binary = new byte[administrators.BinaryLength];
            administrators.GetBinaryForm(binary, 0);
            sid = Marshal.AllocHGlobal(binary.Length);
            Marshal.Copy(binary, 0, sid, binary.Length);
            if (!AddSIDToBoundaryDescriptor(ref boundary, sid))
            {
                Log.Warn("App", $"sin SID en el límite (error {Marshal.GetLastWin32Error()}); se usa solo el mutex local");
                return AcquireLocal(required: true);
            }

            _namespace = CreatePrivateNamespaceW(IntPtr.Zero, boundary, NamespaceAlias);
            if (_namespace == IntPtr.Zero && Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS)
                _namespace = OpenPrivateNamespaceW(boundary, NamespaceAlias);
            if (_namespace == IntPtr.Zero)
            {
                Log.Warn("App", $"sin espacio de nombres privado (error {Marshal.GetLastWin32Error()}); se usa solo el mutex local");
                return AcquireLocal(required: true);
            }

            _private = new Mutex(initiallyOwned: true, PrivateName, out bool createdNew);
            if (createdNew) return true;
            Log.Warn("App", "no se inició: otra instancia elevada ya está en marcha");
            _private.Dispose();
            _private = null;
            return false;
        }
        finally
        {
            if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
            DeleteBoundaryDescriptor(boundary);
        }
    }

    private bool AcquireLocal(bool required)
    {
        try
        {
            _local = new Mutex(initiallyOwned: true, LocalName, out bool createdNew);
            if (createdNew) return true;
            _local.Dispose();
            _local = null;
            if (required) Log.Warn("App", "no se inició: otra instancia ya ocupa el mutex de instancia única");
            else Log.Warn("App", "otro proceso ocupa el mutex local de instancia única; se ignora (el bloqueo válido es el privado)");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists and belongs to an elevated instance.
            if (required) Log.Warn("App", "no se inició: una instancia elevada ya está en marcha");
            else Log.Warn("App", "el mutex local de instancia única no se puede abrir; se ignora");
            return false;
        }
    }

    public void Dispose()
    {
        foreach (Mutex? mutex in new[] { _private, _local })
        {
            if (mutex is null) continue;
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            mutex.Dispose();
        }
        _private = null;
        _local = null;
        if (_namespace != IntPtr.Zero) ClosePrivateNamespace(_namespace, 0);
        _namespace = IntPtr.Zero;
    }
}
