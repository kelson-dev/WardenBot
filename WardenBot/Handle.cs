using Discord;
using Discord.Commands;
using Discord.Rest;
using Discord.WebSocket;
using Humanizer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WardenBot
{
    internal static class Handle
    {
        private static ConcurrentDictionary<ulong, DateTimeOffset> CLEANING_SERVERS = new();

        internal static async Task ManageConfiguration(
            Config config,
            SocketUserMessage message,
            SocketCommandContext context,
            DirectoryInfo configDirectory,
            Func<ulong, Config, Config, bool> tryUpdate)
        {
            var uploaded = message.Attachments.FirstOrDefault(
                a => a.Filename == $"{context.Guild.Id}.json");

            if (uploaded is Attachment foundConfig) // adding an updated config
            {
                using var stream = await FetchAttachment(foundConfig);
                var newConfig = await JsonSerializer.DeserializeAsync<Config>(stream);
                if (tryUpdate(context.Guild.Id, newConfig, config))
                {
                    try
                    {
                        var reserialized = JsonSerializer.Serialize(newConfig);
                        string nextFileName = $"{context.Guild.Id}_next.json";
                        var file = new FileInfo(Path.Combine(configDirectory.FullName, nextFileName));
                        await File.WriteAllTextAsync(file.FullName, reserialized, encoding: Encoding.UTF8);
                        File.Replace(nextFileName, $"{context.Guild.Id}.json", destinationBackupFileName: $"{context.Guild.Id}.json.bak");
                    }
                    catch (IOException)
                    {
                        await message.Channel.SendMessageAsync("Configuration update, but saving failed. Change will be lost if the bot restarts.");
                    }
                }
                else
                {
                    await message.Channel.SendMessageAsync("Configuration update failed");
                }
            }
            else // showing the current config
            {
                using var mem = new MemoryStream();
                await JsonSerializer.SerializeAsync(mem, config);
                mem.Position = 0;
                await message.Channel.SendFileAsync(
                    mem, 
                    $"{context.Guild.Id}.json", 
                    text: "Here is the current config. To change it upload the file with your changes while tagging me and mentioning 'config'");
            }
        }

        internal static async Task OnboardConfiguration(
            Config config,
            SocketUserMessage message,
            SocketCommandContext context,
            DirectoryInfo configDirectory,
            Func<ulong, Config, bool> tryAdd)
        {
            ulong onboardId = 0;
            var uploaded = message.Attachments.SingleOrDefault(
                a => a.Filename.EndsWith(".json") 
                  && ulong.TryParse(a.Filename[..^5], out onboardId));
            if (uploaded is Attachment foundConfig)
            {
                using var stream = await FetchAttachment(foundConfig);
                var newConfig = await JsonSerializer.DeserializeAsync<Config>(stream);
                if (tryAdd(onboardId, newConfig))
                {
                    try
                    {
                        var filename = Path.Combine(configDirectory.FullName, $"{onboardId}.json");
                        var reserialized = JsonSerializer.Serialize(newConfig);
                        File.WriteAllText(filename, reserialized);
                    }
                    catch (IOException)
                    {
                        await message.Channel.SendMessageAsync("Could not save new config, config will be lost if the bot restarts.");
                    }
                }
            }
        }

        internal static async Task SubmissionChannelUserMessage(
            Config config,
            SocketUserMessage message,
            SocketCommandContext context)
        {
            var user = context.Guild.GetUser(message.Author.Id);
            var submissionRole = context.Guild.GetRole(config.SubmissionRoleId);
            if (user != null && submissionRole != null)
            {
                await user.AddRoleAsync(submissionRole);
            }
        }

        const string MEMBERSHIP_REFRESH_AUDIT_REASON = "Automated membership role refresh";
        const string MEMBERSHIP_REMOVE_AUDIT_REASON = "Automated membershp expiration";

        internal static async Task SubmissionChannelReaction(
            Config config, 
            SocketTextChannel textChannel, 
            SocketReaction reaction, 
            SocketUserMessage message)
        {
            var user = textChannel.Guild.GetUser(message.Author.Id);
            var membershipRole = textChannel.Guild.GetRole(config.MembershipRoleId);
            if (user.Roles.Any(r => r.Id == config.SubmissionRoleId))
            {
                var submissionRole = textChannel.Guild.GetRole(config.SubmissionRoleId);
                await user.RemoveRoleAsync(submissionRole);
            }
            if (user.Roles.Any(r => r.Id == config.MembershipRoleId))
            {
                await user.RemoveRoleAsync(membershipRole);
            }
            await user.AddRoleAsync(membershipRole, options: new()
            {
                AuditLogReason = MEMBERSHIP_REFRESH_AUDIT_REASON
            });
        }

        internal static async Task Evict(Config config, ulong guildId, SocketUserMessage message, DiscordSocketClient client)
        {
            var guild = client.GetGuild(guildId);
            if (guild == null)
                return;

            if (CLEANING_SERVERS.Count > 3)
            {
                await message.Channel.SendMessageAsync("Several other servers are up cleaning memberships right now. Please try again later.");
                return;
            }

            if (CLEANING_SERVERS.TryGetValue(guildId, out DateTimeOffset started))
            {
                string timeSinceStart = (DateTimeOffset.Now - started).Humanize();
                await message.Channel.SendMessageAsync($"Cleanup started {timeSinceStart} ago");
                return;
            }

            if (CLEANING_SERVERS.TryAdd(guildId, DateTimeOffset.Now))
            {
                await message.Channel.SendMessageAsync("Begining membership expiration cleanup. To be friendly to discord, this may take a long time.");
                var recentAdditionsTask = LastMonthOfMembershipRoleAdditions(config, guild);

                var downloadTask = client.DownloadUsersAsync(new IGuild[] { guild });

                var recentAdditions = await recentAdditionsTask;
                await downloadTask;

                var membershipChannel = guild.GetTextChannel(config.MembershipChannelId);
                var membershipRole = guild.GetRole(config.MembershipRoleId);

                foreach (var user in membershipChannel.Users)
                {
                    if (!recentAdditions.Contains(user.Id))
                    {
                        await user.RemoveRoleAsync(membershipRole, options: new() { AuditLogReason = MEMBERSHIP_REMOVE_AUDIT_REASON });
                        await Task.Delay(TimeSpan.FromSeconds(3)); // extra padding for rate limiting 
                    }
                }

                await message.Channel.SendMessageAsync("Membership cleanup has completed");
            }

            CLEANING_SERVERS.TryRemove(guildId, out DateTimeOffset _);
        }

        private static async Task<HashSet<ulong>> LastMonthOfMembershipRoleAdditions(Config config, SocketGuild guild)
        {
            HashSet<ulong> roleUpdates = new();
            DateTimeOffset oldestFoundMembershipAddition = DateTimeOffset.Now;
            ulong? beforeId = null;
            do
            {
                var batch = guild.GetAuditLogsAsync(
                    limit: 256,
                    beforeId: beforeId,
                    actionType: ActionType.MemberRoleUpdated);
                await foreach (var entryList in batch)
                {
                    foreach (var entry in entryList)
                    {
                        if (entry.CreatedAt < oldestFoundMembershipAddition)
                        {
                            oldestFoundMembershipAddition = entry.CreatedAt;
                            beforeId = entry.Id;
                        }

                        if (entry.CreatedAt.AddMonths(1) > DateTimeOffset.Now
                            && entry.Data is MemberRoleAuditLogData roleAuditData
                            && roleAuditData.Roles.Any(r => r.Added && r.RoleId == config.MembershipRoleId))
                        {
                            roleUpdates.Add(roleAuditData.Target.Id);
                        }
                    }
                }
            }
            while (oldestFoundMembershipAddition.AddMonths(1) > DateTimeOffset.Now);
            return roleUpdates;
        }

        static internal async Task<Stream> FetchAttachment(Attachment attachment)
        {
            var uri = new Uri(attachment.Url);
            using HttpClient client = new();
            try
            {
                var response = await client.GetAsync(uri);
                return await response.Content.ReadAsStreamAsync();
            }
            catch (Exception)
            {
                uri = new Uri(attachment.ProxyUrl);
                var response = await client.GetAsync(uri);
                return await response.Content.ReadAsStreamAsync();
            }
        }
    }
}
