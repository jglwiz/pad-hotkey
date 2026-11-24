using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using PadHotkey.Core;

namespace PadHotkey;

public sealed class Storage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string DirectoryPath { get; }
    public string Token { get; }
    private string ProfilePath => Path.Combine(DirectoryPath, "profile.json");

    public Storage(string path)
    {
        DirectoryPath = Path.GetFullPath(path);
        var marker = Path.Combine(DirectoryPath, ".padhotkey-data");
        if (Directory.Exists(DirectoryPath) && Directory.EnumerateFileSystemEntries(DirectoryPath).Any() &&
            (!File.Exists(marker) || File.ReadAllText(marker) != "PadHotkey v1"))
            throw new InvalidOperationException("Data path must be empty or an existing PadHotkey data directory; refusing to alter unrelated directory permissions.");
        var directory = Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(marker, "PadHotkey v1");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        string tokenPath = Path.Combine(DirectoryPath, "api-token.txt");
        if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Token = File.ReadAllText(tokenPath).Trim();
        if (Token.Length != 64 || !Token.All(Uri.IsHexDigit)) throw new InvalidOperationException("Invalid API token file.");
    }

    public Profile Load()
    {
        if (!File.Exists(ProfilePath)) Save(new Profile());
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(ProfilePath), Json) ?? throw new InvalidOperationException("Empty profile.");
        ProfileValidator.Validate(profile);
        return profile;
    }

    public void Save(Profile profile)
    {
        ProfileValidator.Validate(profile);
        var temp = Path.Combine(DirectoryPath, $"profile-{Guid.NewGuid():N}.tmp");
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, profile, Json);
            file.Flush(true);
        }
        // Retain every previous revision; never permanently delete a user's configuration.
        if (File.Exists(ProfilePath)) File.Replace(temp, ProfilePath, Path.Combine(DirectoryPath, $"profile-backup-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json"));
        else File.Move(temp, ProfilePath);
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
}
