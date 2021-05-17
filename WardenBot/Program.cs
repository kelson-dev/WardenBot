using System;
using static System.Console;
using static System.Environment;
using System.IO;
using System.Threading.Tasks;
using Discord.WebSocket;
using Discord;
using Discord.Commands;
using WardenBot;
using System.Linq;

WriteLine($"Starting Warden at UTC {DateTime.UtcNow}");
var STARTUP = DateTimeOffset.Now;
var CHECKMARK = new Emoji("✅");
var CONFIGS_DIRECTORY = new DirectoryInfo(GetEnvironmentVariable("configs_path")) ?? new DirectoryInfo(".");
var CONFIGS = await Config.LoadAll(CONFIGS_DIRECTORY);
var CLIENT = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
    | GatewayIntents.GuildPresences
    | GatewayIntents.GuildMessages
    | GatewayIntents.GuildMembers
    | GatewayIntents.GuildMessageReactions,
    GuildSubscriptions = true,
});

await CLIENT.LoginAsync(TokenType.Bot, GetEnvironmentVariable("warden_token") ?? File.ReadAllText("warden_token.key"));

CLIENT.MessageReceived += (SocketMessage message) =>
{
    // If the message is from a user
    if (message is SocketUserMessage userMessage)
    {
        var context = new SocketCommandContext(CLIENT, userMessage);

        // If there is a configuration for the server the message is from
        if (CONFIGS.TryGetValue(context.Guild.Id, out Config config))
        {
            if (userMessage.Channel.Id == config.SubmissionChannelId)
            {
                if (userMessage.Attachments.Count == 0)
                    return Task.CompletedTask;

                return Handle.SubmissionChannelUserMessage(config, userMessage, context);
            }

            if (config.BotConfigurationChannelIds.Contains(userMessage.Channel.Id)
             && userMessage.MentionedUsers.Any(u => u.Id == context.Guild.CurrentUser.Id))
            {
                var text = userMessage.Content.ToUpperInvariant();
                if (text.Contains("CONFIG"))
                {
                    return Handle.ManageConfiguration(config, userMessage, context, CONFIGS_DIRECTORY, CONFIGS.TryUpdate);
                }
                else if (text.Contains("ONBOARD"))
                {
                    return Handle.OnboardConfiguration(config, userMessage, context, CONFIGS_DIRECTORY, CONFIGS.TryAdd);
                }
                else if (text.Contains("EVICT"))
                {
                    return Handle.Evict(config, context.Guild.Id, userMessage, CLIENT);
                }
            }
        }
    }

    return Task.CompletedTask;
};


CLIENT.ReactionAdded += async (Cacheable<IUserMessage, ulong> cache, ISocketMessageChannel channel, SocketReaction reaction) =>
{
    if (reaction.Emote == CHECKMARK
    && channel is SocketTextChannel textChannel
    && CONFIGS.TryGetValue(textChannel.Guild.Id, out Config config))
    {
        var message = await cache.GetOrDownloadAsync();
        if (message is SocketUserMessage userMessage)
        {
            await Handle.SubmissionChannelReaction(config, textChannel, reaction, userMessage);
        }
    }
};