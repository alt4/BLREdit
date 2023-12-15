using BLREdit.API.Export;
using BLREdit.Game;
using BLREdit.Import;
using BLREdit.UI;
using BLREdit.UI.Views;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace BLREdit.Export;

public sealed class ExportSystem
{
    private static FileInfo? _currentBackupFile;
    public static FileInfo CurrentBackupFile { get { _currentBackupFile ??= new FileInfo($"Backup\\{DateTime.Now:yy-MM-dd-HH-mm-ss}.zip"); return _currentBackupFile; } }

    public static void CopyMagiCowToClipboard(BLRLoadoutStorage loadout)
    {
        var magiProfile = new MagiCowsProfile { PlayerName = loadout.Shareable.Name };
        loadout.BLR.Write(magiProfile);

        string clipboard = $"register{Environment.NewLine}{IOResources.RemoveWhiteSpacesFromJson.Replace(IOResources.Serialize(magiProfile, true), "$1")}";

        try
        {
            SetClipboard(clipboard);
            LoggingSystem.Log("Clipboard copy succes!");
        }
        catch (Exception error)
        { LoggingSystem.Log($"failed CopyToClipboard {error}"); }
    }

    public static void SetClipboard(string value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value), "value to beinserted into clipboard was null");

        Process clipboardExecutable = new()
        {
            StartInfo = new ProcessStartInfo
            {
                RedirectStandardInput = true,
                FileName = @"clip",
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        clipboardExecutable.Start();
        clipboardExecutable.StandardInput.Write(value);
        clipboardExecutable.StandardInput.Close();
    }

    public static string? GetClipboard()
    {
        if (Clipboard.ContainsText(TextDataFormat.Text))
        {
            return Clipboard.GetText(TextDataFormat.Text);
        }
        return null;
    }

    public static ObservableCollectionExtended<BLRLoadoutStorage> LoadStorage()
    {
        LoggingSystem.Log("Started Loading Shareable and BLR Profile Combos");
        if (DataStorage.ShareableProfiles.Count < 1)
        {
            DataStorage.ShareableProfiles.Add(new ShareableProfile());
        }
        BLRLoadoutStorage[] storage = new BLRLoadoutStorage[DataStorage.ShareableProfiles.Count];
        for (int i = 0; i < DataStorage.ShareableProfiles.Count; i++)
        {
            DataStorage.ShareableProfiles[i].TimeOfCreation = i;
            DataStorage.ShareableProfiles[i].RegisterWithChildren();
            storage[i] = new(DataStorage.ShareableProfiles[i]);
        }

        LoggingSystem.Log("Finished Loading Shareable and BLR Profile Combos");
        return new(storage);
    }

    public static ObservableCollectionExtended<ShareableProfile> LoadShareableProfiles()
    {
        LoggingSystem.Log("Started Loading ShareableProfiles");
        ImportSystem.Initialize();

        LoggingSystem.Log($"Backup:{CurrentBackupFile.FullName}");
        LoggingSystem.Log("Compressing Profile folder!");
        LoggingSystem.ResetWatch();
        ZipFile.CreateFromDirectory($"{IOResources.PROFILE_DIR}", CurrentBackupFile.FullName, CompressionLevel.Optimal, false);
        LoggingSystem.PrintElapsedTime("Finished Compressing in {0}ms");

        var profiles = IOResources.DeserializeFile<ObservableCollectionExtended<ShareableProfile>>($"{IOResources.PROFILE_DIR}profileList.json") ?? new();

        foreach (string file in Directory.EnumerateFiles($"{IOResources.PROFILE_DIR}"))
        {
            var esProfile = IOResources.DeserializeFile<ExportSystemProfile>(file);
            if (esProfile is null)
            {
                var mcProfile = IOResources.DeserializeFile<MagiCowsOldProfile>(file);
                if (mcProfile is not null)
                {
                    LoggingSystem.Log("Found an old profile converting it to new profile format");
                    esProfile = mcProfile.ConvertToNew();
                }
            }

            if (esProfile is not null && esProfile.IsHealthOkAndRepair())
            {
                profiles.Add(esProfile.ConvertToShareable());
                File.Delete(file);
            }
        }
        LoggingSystem.Log("Finished Loading ShareableProfiles");
        return profiles;
    }

    public static void UpdateOrAddProfileSettings(string profileName, BLRProfileSettingsWrapper settings)
    {
        if (settings is null) return;

        if (DataStorage.ProfileSettings.TryGetValue(profileName, out var oldProfile))
        {
            if (settings.PlayTime >= oldProfile.PlayTime)
            {
                DataStorage.ProfileSettings[profileName] = settings;
            }  
        }
        else
        {
            DataStorage.ProfileSettings.Add(profileName, settings);
        }
    }

    public static BLRProfileSettingsWrapper GetOrAddProfileSettings(string profileName)
    {
        if (DataStorage.ProfileSettings.TryGetValue(profileName, out var value))
        {
            return value;
        }
        else
        {
            List<BLRProfileSettingsWrapper> settingsWrappers = new();
            foreach (var client in DataStorage.GameClients)
            {
                client.UpdateProfileSettings();
            }
            if (settingsWrappers.Count > 0)
            {
                foreach (var filteredSetting in settingsWrappers)
                {
                    DataStorage.ProfileSettings.Add(filteredSetting.ProfileName, filteredSetting);
                }
            }

            if (DataStorage.ProfileSettings.TryGetValue(profileName, out var valu))
            {
                return valu;
            }
            else
            {
                LoggingSystem.Log($"[ProfileSettings]({profileName}): creating new profile");
                var newProfile = new BLRProfileSettingsWrapper(profileName, null, null);
                DataStorage.ProfileSettings.Add(profileName, newProfile);
                return newProfile;
            }
        }
    }
}

public sealed class BLRLoadoutStorage(ShareableProfile share, BLRProfile? blr = null)
{
    public ShareableProfile Shareable { get; } = share;
    private BLRProfile? blr = blr;
    public BLRProfile BLR { get { blr ??= Shareable.ToBLRProfile(); return blr; } }
    static bool isExchanging = false;
    public static event EventHandler? ProfileGotRemoved;
    public void Remove()
    {
        
        int indexShare = DataStorage.ShareableProfiles.IndexOf(Shareable);
        int indexLoadout = DataStorage.Loadouts.IndexOf(this);
        LoggingSystem.Log($"Removing({indexShare}, {indexLoadout}): {Shareable.Name}");
        UndoRedoSystem.DoAction(() => { DataStorage.ShareableProfiles.Remove(Shareable); }, () => { DataStorage.ShareableProfiles.Insert(indexShare, Shareable); });
        UndoRedoSystem.DoAction(() => { DataStorage.Loadouts.Remove(this); }, () => { DataStorage.Loadouts.Insert(indexLoadout, this); });
        UndoRedoSystem.EndUndoRecord();
        ProfileGotRemoved?.Invoke(this, new EventArgs());
    }

    public static void Exchange(int from, int to)
    {
        if (from == to) return;
        if (isExchanging) return;
        isExchanging = true;
        DataStorage.ShareableProfiles.Exchange(from, to);
        DataStorage.Loadouts.Exchange(from, to);
        DataStorage.ShareableProfiles.SignalExchange();
        DataStorage.Loadouts.SignalExchange();
        isExchanging = false;
    }

    public void ApplyLoadout(BLRClient? client)
    {
        if (client is not null)
        {
            var directory = $"{client.ConfigFolder}profiles\\";
            Directory.CreateDirectory(directory);
            if (client.ProxyVersion == "BLRevive")
            {
                IOResources.SerializeFile($"{directory}{DataStorage.Settings.PlayerName}.json", new[] { new LMLoadout(BLR.Loadout1, "Loadout 1"), new LMLoadout(BLR.Loadout2, "Loadout 2"), new LMLoadout(BLR.Loadout3, "Loadout 3") });
                MainWindow.ShowAlert($"Applied BLRevive Loadouts!\nScroll through your loadouts to\nrefresh ingame Loadouts!", 8); //TODO: Add Localization
            }
            else
            {
                IOResources.SerializeFile($"{directory}{DataStorage.Settings.PlayerName}.json", new[] { new LoadoutManagerLoadout(BLR.Loadout1), new LoadoutManagerLoadout(BLR.Loadout2), new LoadoutManagerLoadout(BLR.Loadout3) });
                MainWindow.ShowAlert($"Applied Proxy Loadouts!\nScroll through your loadouts to\nrefresh ingame Loadouts!", 8); //TODO: Add Localization
            }
            
            if (MainWindow.Instance is not null)
            {
                MainWindow.Instance.ProfileComboBox.SelectedIndex = MainWindow.Instance.ProfileComboBox.Items.IndexOf(Shareable);
                DataStorage.Settings.CurrentlyAppliedLoadout = MainWindow.Instance.ProfileComboBox.SelectedIndex;
                Shareable.LastApplied = DateTime.Now;
            }
            BLR.IsChanged = false;
        }
    }

    private ICommand? applyLoadoutCommand;
    [JsonIgnore]
    public ICommand ApplyLoadoutCommand
    {
        get
        {
            applyLoadoutCommand ??= new RelayCommand(
                    param => ApplyLoadout(DataStorage.Settings.DefaultClient)
                );
            return applyLoadoutCommand;
        }
    }

    private ICommand? removeLoadoutCommand;
    [JsonIgnore]
    public ICommand RemoveLoadoutCommand
    {
        get
        {
            removeLoadoutCommand ??= new RelayCommand(
                    param => Remove()
                );
            return removeLoadoutCommand;
        }
    }

    public static BLRLoadoutStorage AddNewLoadoutSet(string Name = "New Loadout Set!", BLRProfile? profile = null, ShareableProfile? shareable = null)
    {
        var share = shareable ?? new ShareableProfile() { Name = Name };
        share.RegisterWithChildren();
        var blr = profile ?? share.ToBLRProfile();
        profile?.Write(share);
        var loadout = new BLRLoadoutStorage(share, blr);
        DataStorage.ShareableProfiles.Add(share);
        DataStorage.Loadouts.Add(loadout);
        return loadout;
    }
}