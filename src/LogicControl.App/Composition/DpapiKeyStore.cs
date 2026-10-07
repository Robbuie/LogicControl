// UseWPF drops System.IO from the implicit usings; this file reads and writes files.
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LogicControl.App.ViewModels.Assistant;

namespace LogicControl.App.Composition;

/// <summary>
/// Keeps the Anthropic API key encrypted with Windows DPAPI for the current user, in
/// %LOCALAPPDATA%\LogicControl\assistant.key. Another Windows account on the same machine, or the
/// same file copied to another machine, cannot decrypt it.
///
/// <para>P/Invoke into crypt32 rather than System.Security.Cryptography.ProtectedData, because
/// that is a NuGet package and src/ takes none; the two calls it needs are a few lines.</para>
///
/// <para>An <c>ANTHROPIC_API_KEY</c> environment variable is used when no key has been saved -
/// the same variable the SDKs and Claude Code read, so a machine already set up for one works
/// here with nothing to type.</para>
/// </summary>
public sealed class DpapiKeyStore : IApiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LogicControl assistant key");

    private readonly string _path;

    public DpapiKeyStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.Data, "assistant.key");

    public string? Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                byte[] clear = Unprotect(File.ReadAllBytes(_path));
                return Encoding.UTF8.GetString(clear);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Unreadable - a profile restored onto another machine. Fall through to the variable.
        }

        return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } fromEnv ? fromEnv : null;
    }

    public void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, Protect(Encoding.UTF8.GetBytes(key)));
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static byte[] Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var entropy = new DataBlob();
        var output = new DataBlob();
        GCHandle inHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        GCHandle entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);

        try
        {
            input.cbData = data.Length;
            input.pbData = inHandle.AddrOfPinnedObject();
            entropy.cbData = Entropy.Length;
            entropy.pbData = entropyHandle.AddrOfPinnedObject();

            bool ok = protect
                ? CryptProtectData(ref input, "LogicControl", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);

            if (!ok)
            {
                throw new InvalidOperationException($"Windows could not {(protect ? "encrypt" : "decrypt")} the key (error {Marshal.GetLastWin32Error()}).");
            }

            byte[] result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

/// <summary>The assistant panel's own choices - model, open or closed - kept between runs.</summary>
public sealed class AssistantPreferences
{
    public string? Model { get; set; }

    public bool Open { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Data, "assistant.json");

    public static AssistantPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AssistantPreferences>(File.ReadAllText(FilePath)) ?? new AssistantPreferences()
                : new AssistantPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AssistantPreferences();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that did not save is not worth a dialog.
        }
    }
}
