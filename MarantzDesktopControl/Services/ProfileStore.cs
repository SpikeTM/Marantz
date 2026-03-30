using System.IO;
using System.Text.Json;
using MarantzDesktopControl.Models;

namespace MarantzDesktopControl.Services;

public sealed class ProfileStore
{
    private readonly string _dataDirectory;
    private readonly string _profilesPath;

    public ProfileStore()
    {
        _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarantzDesktopControl");
        _profilesPath = Path.Combine(_dataDirectory, "profiles.json");
    }

    public async Task<List<ReceiverProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_profilesPath))
        {
            return new List<ReceiverProfile>();
        }

        await using FileStream stream = File.OpenRead(_profilesPath);
        var data = await JsonSerializer.DeserializeAsync<List<ReceiverProfile>>(stream, cancellationToken: cancellationToken);
        return data ?? new List<ReceiverProfile>();
    }

    public async Task SaveProfilesAsync(IEnumerable<ReceiverProfile> profiles, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        await using FileStream stream = File.Create(_profilesPath);
        await JsonSerializer.SerializeAsync(stream, profiles.ToList(), new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
