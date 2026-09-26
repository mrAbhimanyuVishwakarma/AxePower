using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AxePower.App.ViewModels.ShareScreen;
using AxePower.Core.Models.Friends;
using AxePower.Core.Models.ShareScreen;
using AxePower.Core.Services;
using AxePower.Core.Services.ShareScreen;
using Xunit;

namespace AxePower.Tests.Friends;

public class FriendsHubTests
{
    internal class FakeShareService : IShareScreenService
    {
        public ShareSessionStatus Status => ShareSessionStatus.Idle;
        public event EventHandler<ShareSessionStatus>? StatusChanged { add { } remove { } }
        public Task<ShareSessionStatus> StartAsync(ShareScreenSettings settings, IProgress<string>? step, CancellationToken ct) =>
            Task.FromResult(ShareSessionStatus.Idle);
        public Task<ShareSessionStatus> StartRoomAsync(ShareScreenSettings settings, CancellationToken ct) => Task.FromResult(ShareSessionStatus.Idle);
        public Task StopScreenShareAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public string? SetRemoteControl(bool enabled) => null;
        public event EventHandler<ShareChatMessage>? ChatMessageReceived { add { } remove { } }
        public void SendChatMessage(ShareChatMessage message) { }
        public event EventHandler<byte[]>? VoicePacketReceived { add { } remove { } }
        public void SendVoicePacket(byte[] opusPacket) { }
    }

    internal class FakeDeviceProvider : IShareDeviceProvider
    {
        public IReadOnlyList<CaptureDisplay> GetDisplays() => Array.Empty<CaptureDisplay>();
        public IReadOnlyList<ShareAudioDevice> GetRenderDevices() => Array.Empty<ShareAudioDevice>();
        public IReadOnlyList<ShareAudioDevice> GetCaptureDevices() => Array.Empty<ShareAudioDevice>();
    }

    internal class FakeToolProvider : IShareToolProvider
    {
        public ShareToolStatus FindFfmpeg() => ShareToolStatus.Missing("ffmpeg");
        public ShareToolStatus FindMediaMtx() => ShareToolStatus.Missing("mediamtx");
        public Task<ShareToolStatus> EnsureFfmpegAsync(IProgress<double>? progress, CancellationToken ct) =>
            Task.FromResult(ShareToolStatus.Missing("ffmpeg"));
        public Task<ShareToolStatus> EnsureMediaMtxAsync(IProgress<double>? progress, CancellationToken ct) =>
            Task.FromResult(ShareToolStatus.Missing("mediamtx"));
    }

    [Fact]
    public void FriendStatus_And_SavedFriend_Properties()
    {
        var friend = new SavedFriend
        {
            Name = "Valkyrie",
            Address = "http://100.64.1.55:8555/stream",
            Status = FriendStatus.Streaming,
            Activity = "Streaming Cyberpunk 2077",
            IsStreaming = true,
            StreamUrl = "http://100.64.1.55:8555/stream"
        };

        Assert.Equal("V", friend.Initials);
        Assert.Equal(FriendStatus.Streaming, friend.Status);
        Assert.Equal("#A855F7", friend.StatusBadgeColor);
        Assert.Equal("Streaming Cyberpunk 2077", friend.StatusDisplay);
        Assert.True(friend.IsStreaming);

        var offlineFriend = new SavedFriend
        {
            Name = "Shadow",
            Status = FriendStatus.Offline
        };
        Assert.Equal("#80848E", offlineFriend.StatusBadgeColor);
        Assert.Equal("Offline", offlineFriend.StatusDisplay);
    }

    [Fact]
    public void FriendsProfile_Defaults_ToCurrentDeviceIdentity()
    {
        var profile = new FriendsProfile
        {
            GamerTag = "GhostRider"
        };

        Assert.Equal("G", profile.Initials);
        Assert.Equal(FriendStatus.Online, profile.Status);
    }

    [Fact]
    public void FriendsViewModel_Filtering_And_Search()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_FriendsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var log = new LogService();
            var settingsService = new SettingsService(log, tempDir);
            var settingsVm = new ShareScreenSettingsViewModel(
                new FakeDeviceProvider(), new FakeToolProvider(), settingsService, log);
            var chatStore = new ShareChatStore(Path.Combine(tempDir, "Chat"), log);
            var vm = new ShareScreenViewModel(
                new FakeShareService(), settingsVm, new NotificationService(), log, chatStore);

            // Add sample squad
            var f1 = new SavedFriend { Name = "Alpha", Status = FriendStatus.Online, Address = "http://10.0.0.1:8555" };
            var f2 = new SavedFriend { Name = "Bravo", Status = FriendStatus.Streaming, IsStreaming = true, Address = "http://10.0.0.2:8555" };
            var f3 = new SavedFriend { Name = "Charlie", Status = FriendStatus.Offline, Address = "http://10.0.0.3:8555" };

            vm.SavedFriends.Add(f1);
            vm.SavedFriends.Add(f2);
            vm.SavedFriends.Add(f3);

            // Filter: Online (f1 and f2 should be included, f3 is offline)
            vm.SetFriendsFilterCommand.Execute("Online");
            var onlineFriends = vm.FilteredFriends.ToList();
            Assert.Equal(2, onlineFriends.Count);
            Assert.Contains(onlineFriends, f => f.Name == "Alpha");
            Assert.Contains(onlineFriends, f => f.Name == "Bravo");
            Assert.DoesNotContain(onlineFriends, f => f.Name == "Charlie");

            // Filter: Live / Streams (only f2)
            vm.SetFriendsFilterCommand.Execute("Streams");
            var liveFriends = vm.FilteredFriends.ToList();
            Assert.Single(liveFriends);
            Assert.Equal("Bravo", liveFriends[0].Name);

            // Filter: All (f1, f2, f3)
            vm.SetFriendsFilterCommand.Execute("All");
            var allFriends = vm.FilteredFriends.ToList();
            Assert.Equal(3, allFriends.Count);

            // Search query
            vm.FriendSearchText = "alp";
            var searched = vm.FilteredFriends.ToList();
            Assert.Single(searched);
            Assert.Equal("Alpha", searched[0].Name);

            vm.FriendSearchText = string.Empty;
            Assert.Equal(3, vm.FilteredFriends.Count());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TheMutedMicrophone_IsASlashedMic_NotTheSearchGlyph()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_FriendsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var log = new LogService();
            var settingsVm = new ShareScreenSettingsViewModel(
                new FakeDeviceProvider(), new FakeToolProvider(), new SettingsService(log, tempDir), log);
            var vm = new ShareScreenViewModel(
                new FakeShareService(), settingsVm, new NotificationService(), log, new ShareChatStore(Path.Combine(tempDir, "Chat"), log));

            Assert.Equal("", vm.MicGlyph);   // microphone
            vm.IsMicMuted = true;
            Assert.Equal("", vm.MicGlyph);   // slashed microphone; E721 would be the search magnifier
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FriendsViewModel_Commands_Message_And_Remove()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AxePower_FriendsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var log = new LogService();
            var settingsService = new SettingsService(log, tempDir);
            var settingsVm = new ShareScreenSettingsViewModel(
                new FakeDeviceProvider(), new FakeToolProvider(), settingsService, log);
            var chatStore = new ShareChatStore(Path.Combine(tempDir, "Chat"), log);
            var vm = new ShareScreenViewModel(
                new FakeShareService(), settingsVm, new NotificationService(), log, chatStore);

            var friend = new SavedFriend { Name = "NeonKnight", Address = "http://192.168.1.100:8555/stream" };
            vm.SavedFriends.Add(friend);

            // Message friend
            vm.MessageFriendCommand.Execute(friend);
            Assert.True(vm.IsChatOpen);
            Assert.Equal("NeonKnight", vm.ChannelName);

            // Remove friend
            vm.RemoveFriendCommand.Execute(friend);
            Assert.DoesNotContain(friend, vm.SavedFriends);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

}
