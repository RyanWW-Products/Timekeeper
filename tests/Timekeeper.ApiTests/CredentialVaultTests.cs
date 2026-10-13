using System.Runtime.InteropServices;
using Timekeeper.App;

static class CredentialVaultTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("legacy null empty credential blobs load as optional blank tokens", () =>
        {
            Equal("", CredentialVault.DecodeBlob(IntPtr.Zero, 0));
            return Task.CompletedTask;
        });
        await check("zero byte credentials never dereference their blob pointer", () =>
        {
            Equal("", CredentialVault.DecodeBlob(new IntPtr(1), 0));
            return Task.CompletedTask;
        });
        await check("saved Unicode token decoding respects the exact blob size", () =>
        {
            var pointer = Marshal.StringToHGlobalUni("test-token-\u00e9-extra");
            try { Equal("test-token-\u00e9", CredentialVault.DecodeBlob(pointer, 24)); }
            finally { Marshal.FreeHGlobal(pointer); }
            return Task.CompletedTask;
        });
        await check("nonempty credentials with a missing pointer produce recovery instructions", () =>
        {
            Invalid(IntPtr.Zero, 2);
            return Task.CompletedTask;
        });
        await check("malformed and oversized credential lengths fail before touching memory", () =>
        {
            Invalid(new IntPtr(1), 3);
            Invalid(new IntPtr(1), 2562);
            Invalid(new IntPtr(1), uint.MaxValue);
            return Task.CompletedTask;
        });
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new Exception("Credential decoding did not match the expected value.");
    }
    private static void Invalid(IntPtr pointer, uint length)
    {
        try { CredentialVault.DecodeBlob(pointer, length); }
        catch (InvalidOperationException error) when (error.Message.Contains("Open Settings")) { return; }
        throw new Exception("Expected instructions to re-save an invalid credential.");
    }
}
