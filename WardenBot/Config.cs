using System.Collections.Concurrent;
using System.IO;
using static System.Console;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;

namespace WardenBot
{
    internal record Config(
        ulong MembershipRoleId,
        ulong MembershipChannelId,
        ulong SubmissionRoleId,
        ulong SubmissionChannelId,
        HashSet<ulong> ApproverRoleIds,
        HashSet<ulong> BotAdministratorRoleIds,
        HashSet<ulong> BotAdministratorUserIds,
        HashSet<ulong> BotConfigurationChannelIds,
        int MembershipExpirationCheckIntervalDays)
    {
        internal static async Task<ConcurrentDictionary<ulong, Config>> LoadAll(DirectoryInfo configsDirectory)
        {
            ConcurrentDictionary<ulong, Config> map = new();
            foreach (var file in configsDirectory.EnumerateFiles("?.json"))
            {
                if (ulong.TryParse(file.Name[..^5], out ulong guildId))
                {
                    var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(file.FullName));
                    if (map.TryAdd(guildId, config))
                        WriteLine($"Loaded guild config for {guildId}");
                }
            }
            return map;
        }
    }

}
