using System.Security.Cryptography;
using System.Text;

// Release signing for DataField Vietnam. Run on the release machine, never on the server.
//
// The private key this creates is the only thing standing between a compromised file server and
// arbitrary code execution on every player who updates, so it does not belong anywhere near that
// server -- not in the repo, not in update_files, not in a backup that syncs to it. It lives under
// %APPDATA% by default, encrypted with a passphrase.
//
//   dfvsign keygen                       create the signing key and print the public half
//   dfvsign sign <file> <version>        write <file>.sig next to <file>
//   dfvsign verify <file>                check <file> against <file>.sig, as a client would

const string KeyEnvironmentVariable = "DFV_SIGNING_KEY";

var defaultKeyPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "DataFieldVietnam", "signing", "release-key.p8");

var keyPath = Environment.GetEnvironmentVariable(KeyEnvironmentVariable) ?? defaultKeyPath;

try
{
    return args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
    {
        "keygen" => KeyGen(),
        "sign" when args.Length == 3 => Sign(args[1], args[2]),
        "sign" when args.Length == 4 && args[3] == "--force" => Sign(args[1], args[2], force: true),
        "verify" when args.Length == 2 => Verify(args[1]),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        dfvsign -- release signing for DataField Vietnam

          dfvsign keygen                  create a signing key and print the public half to compile in
          dfvsign sign <file> <version>   sign a release, writing <file>.sig beside it
          dfvsign verify <file>           verify <file> against <file>.sig exactly as a client would

        The key is read from the DFV_SIGNING_KEY environment variable, or from
        %APPDATA%\DataFieldVietnam\signing\release-key.p8 by default.

        Upload the .exe AND its .sig to the server's update_files folder. Never upload the key.
        """);
    return 1;
}

int KeyGen()
{
    // A key in the game folder, the repository or the server's upload staging is a key that gets
    // zipped, committed or copied to the box sooner or later. Refuse before one exists rather than
    // after it has signed something, because at that point moving it is no longer enough.
    var directory = Path.GetFullPath(Path.GetDirectoryName(keyPath) ?? ".");
    foreach (var unsafePart in new[] { "Battlefield Vietnam", "update_files", "dfbfv" })
    {
        if (directory.Contains(unsafePart, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"error: refusing to create a signing key under '{unsafePart}'.");
            Console.Error.WriteLine($"       {directory}");
            Console.Error.WriteLine("That folder gets shared, committed or copied to the server. Unset");
            Console.Error.WriteLine($"{KeyEnvironmentVariable} to use the default location instead:");
            Console.Error.WriteLine($"       {defaultKeyPath}");
            return 1;
        }
    }

    if (File.Exists(keyPath))
    {
        Console.Error.WriteLine($"error: a key already exists at {keyPath}.");
        Console.Error.WriteLine("Refusing to overwrite it: every release signed with it would stop verifying.");
        Console.Error.WriteLine("Move it aside by hand if you really mean to start over.");
        return 1;
    }

    var passphrase = PromptForPassphrase(confirm: true);

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);

    var encrypted = ecdsa.ExportEncryptedPkcs8PrivateKey(
        passphrase,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
    File.WriteAllBytes(keyPath, encrypted);

    var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    Console.WriteLine($"""

        Signing key written to
            {keyPath}

        BACK IT UP NOW, somewhere offline, and remember the passphrase. If you lose either, you can
        never sign another update that existing clients will accept -- they would all have to be
        reinstalled by hand. If someone else gets both, they can push code to every one of your users.

        Never copy this file to the server.

        Now paste the public half into ReleaseSignature.PublicKeyBase64
        (DataField42.Core/Services/ReleaseSignature.cs) and rebuild:

            public const string PublicKeyBase64 = "{publicKey}";

        """);
    return 0;
}

int Sign(string filePath, string versionText, bool force = false)
{
    if (!File.Exists(filePath))
        throw new FileNotFoundException($"No such file: {filePath}");
    if (!Version.TryParse(versionText, out var version))
        throw new ArgumentException($"'{versionText}' is not a version number.");

    // The version is the only thing standing between a recorded signature and a replay, and it is
    // asserted here by hand. Sign a build as 9.9.9.9 by mistake and that document outranks every
    // client that will ever exist -- permanently, because it is genuinely signed and there is no way
    // to take it back. So check the number against what the binary says about itself.
    var declared = TryReadFileVersion(filePath);
    if (!force && declared != null && declared != version)
        throw new ArgumentException(
            $"{Path.GetFileName(filePath)} reports version {declared}, but you asked to sign it as {version}. " +
            "Signing a version the build does not carry cannot be undone -- fix the version and rebuild, " +
            "or pass --force if you are certain.");

    using var ecdsa = LoadPrivateKey();

    var manifest = new ReleaseSignature.Manifest(
        Path.GetFileName(filePath), version, ReleaseSignature.ComputeSha256Hex(filePath));

    var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(manifest.ToLine()), HashAlgorithmName.SHA256);
    var document = $"{manifest.ToLine()}\n{Convert.ToBase64String(signature)}\n";

    var signaturePath = filePath + ".sig";
    File.WriteAllText(signaturePath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    Console.WriteLine($"signed  {manifest.FileName}  {version}  sha256={manifest.Sha256Hex}");
    Console.WriteLine($"wrote   {signaturePath}");
    Console.WriteLine();
    Console.WriteLine("Upload both the file and this .sig to update_files on the server.");
    return 0;
}

int Verify(string filePath)
{
    var signaturePath = filePath + ".sig";
    if (!File.Exists(signaturePath))
        throw new FileNotFoundException($"No signature beside the file: {signaturePath}");

    if (!ReleaseSignature.IsConfigured)
    {
        Console.Error.WriteLine("error: ReleaseSignature.PublicKeyBase64 is empty in this build.");
        Console.Error.WriteLine("Run 'dfvsign keygen', paste the public key in, and rebuild.");
        return 1;
    }

    // Deliberately the same call the client makes, so this proves the real path rather than a
    // re-implementation of it that could agree with a bug.
    ReleaseSignature.VerifyFile(filePath, File.ReadAllText(signaturePath), Path.GetFileName(filePath), mustBeNewerThan: null);

    var manifest = ReleaseSignature.ParseManifest(File.ReadAllText(signaturePath).Split('\n')[0]);
    Console.WriteLine($"OK  {manifest.FileName}  {manifest.Version}  sha256={manifest.Sha256Hex}");
    return 0;
}

/// <summary>The version a PE carries in its own resources, or null when there is not one to read.</summary>
static Version? TryReadFileVersion(string filePath)
{
    try
    {
        var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.GetFullPath(filePath)).FileVersion;
        return fileVersion != null && Version.TryParse(fileVersion, out var parsed) ? parsed : null;
    }
    catch (Exception)
    {
        return null;   // not a PE, or no version resource: fall back to trusting the argument
    }
}

ECDsa LoadPrivateKey()
{
    if (!File.Exists(keyPath))
        throw new FileNotFoundException(
            $"No signing key at {keyPath}. Run 'dfvsign keygen' first, or point {KeyEnvironmentVariable} at it.");

    var passphrase = PromptForPassphrase(confirm: false);
    var ecdsa = ECDsa.Create();
    try
    {
        ecdsa.ImportEncryptedPkcs8PrivateKey(passphrase, File.ReadAllBytes(keyPath), out _);
    }
    catch (CryptographicException)
    {
        ecdsa.Dispose();
        throw new CryptographicException("Wrong passphrase, or the key file is damaged.");
    }
    return ecdsa;
}

static ReadOnlySpan<char> PromptForPassphrase(bool confirm)
{
    var first = ReadHidden("Passphrase: ");
    if (!confirm)
        return first;

    if (first.Length < 12)
        throw new ArgumentException("Use at least 12 characters -- this key can push code to every user.");
    if (!string.Equals(first, ReadHidden("Confirm:    "), StringComparison.Ordinal))
        throw new ArgumentException("The two passphrases do not match.");
    return first;

    static string ReadHidden(string prompt)
    {
        Console.Write(prompt);

        // Piped in rather than typed: there is no console to hide the echo of, and ReadKey would throw.
        // Worth supporting so a release can be scripted, though typing it leaves fewer copies about.
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        Console.WriteLine();
        return builder.ToString();
    }
}
