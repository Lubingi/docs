using System.Runtime.InteropServices;
using System.Text;
using static LiveSubtitles.App.Interop.NativeMethods;

namespace LiveSubtitles.App.Services;

/// <summary>Stores the OpenAI API key in Windows Credential Manager (per user, encrypted by Windows). Never written to disk by the app.</summary>
public static class CredentialStore
{
    private const string Target = "LiveSubtitles/OpenAI-API-Key";

    public static void SaveApiKey(string key)
    {
        var bytes = Encoding.Unicode.GetBytes(key.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "openai",
                Comment = "OpenAI API key for Live Subtitles",
            };
            if (!CredWrite(ref cred, 0)) throw new InvalidOperationException($"CredWrite failed ({Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static string? LoadApiKey()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static bool HasApiKey() => LoadApiKey() is { Length: > 0 };

    public static void DeleteApiKey() => CredDelete(Target, CRED_TYPE_GENERIC, 0);
}
