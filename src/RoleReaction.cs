using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.EventArgs;
using DSharpPlus.Entities;
using DSharpPlus.CommandsNext;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.Interactivity;
using DSharpPlus.CommandsNext.Attributes;
using DevExchangeBot.Storage;

namespace DevExchangeBot.RoleMenuSystem
{
    public class RoleReaction : BaseCommandModule
    {
        [Command("roleMenuAdd"), Aliases("rma")]
        [Description("Adds a role to the Role Menu (@Role, :emoji:)")]
        [RequireUserPermissions(Permissions.Administrator)]
        public async Task AddRole(CommandContext _ctx, DiscordRole _role, DiscordEmoji _emoji)
        {
            // Add the role and emoji to the list
            StorageContext.Model.RoleMenu.AddRole(_role, _emoji);

            // Update the Role Menu
            DiscordChannel channel = await _ctx.Client.GetChannelAsync(StorageContext.Model.RoleMenu.RoleMenuChannelID);
            await UpdateRoleMenu(await channel.GetMessageAsync(StorageContext.Model.RoleMenu.RoleMenuMsgID));

            // Feedback
            await _ctx.RespondAsync($"Added role: {_role.Name} {_emoji}");
        }

        [Command("roleMenuCreate"), Aliases("rmc")]
        [Description("Creates a Role Menu in this channel")]
        [RequireUserPermissions(Permissions.Administrator)]
        public async Task CreateRoleMenu(CommandContext _ctx)
        {
            DiscordEmbed embed = CreateMenuEmbed(_ctx.Guild);

            DiscordMessage msg = await _ctx.Client.SendMessageAsync(_ctx.Channel, embed);

            foreach (DiscordEmoji emoji in StorageContext.Model.RoleMenu.GetAllEmojis())
            {
                await msg.CreateReactionAsync(emoji);
            }

            StorageContext.Model.RoleMenu.RoleMenuMsgID = msg.Id;
            StorageContext.Model.RoleMenu.RoleMenuChannelID = msg.ChannelId;
        }

        // Just in case for some reason you want to switch the Role Menu
        // without creating a new one
        [Command("roleMenuMessage"), Aliases("rmm")]
        [Description("Changes on which message the Role Menu should display (#channel, ulong MsgID)")]
        [RequireUserPermissions(Permissions.Administrator)]
        public async Task ChangeMessage(CommandContext _ctx, DiscordChannel _channel, ulong _msgID)
        {
            // Get the message
            DiscordMessage msg = await _channel.GetMessageAsync(_msgID);

            // Assign the new message id and channel id
            StorageContext.Model.RoleMenu.RoleMenuMsgID = msg.Id;
            StorageContext.Model.RoleMenu.RoleMenuChannelID = msg.ChannelId;

            // Update Role Menu on the new message
            await UpdateRoleMenu(msg);
        }

        private static async Task OnReacted(DiscordClient _sender, MessageReactionAddEventArgs _event)
        {
            if (StorageContext.Model.RoleMenu.RoleMenuMsgID == 0 || StorageContext.Model.RoleMenu.RoleMenuChannelID == 0) return; // Role Menu Not Created
            if (_event.User.IsBot || _event.Message.Id != StorageContext.Model.RoleMenu.RoleMenuMsgID) return; // Reaction Not On Role Menu

            // Make sure the Role exists
            if (!StorageContext.Model.RoleMenu.GetRoleID(_event.Emoji, out ulong _roleID))
            {
                await _event.Message.DeleteReactionsEmojiAsync(_event.Emoji);
                return;
            }

            DiscordMember member = (DiscordMember)_event.User;

            // Grant the Role and delete the Reaction
            await member.GrantRoleAsync(_event.Guild.GetRole(_roleID)).ConfigureAwait(false);
            await _event.Message.DeleteReactionAsync(_event.Emoji, _event.User).ConfigureAwait(false);
        }

        private static async Task UpdateRoleMenu(DiscordMessage _msg)
        {
            // Update the message
            DiscordEmbed embed = CreateMenuEmbed(_msg.Channel.Guild);

            await _msg.ModifyAsync(embed).ConfigureAwait(false);

            // Set up reactions again
            await _msg.DeleteAllReactionsAsync();

            // Add Role Reactions
            foreach (DiscordEmoji emoji in StorageContext.Model.RoleMenu.GetAllEmojis())
            {
                await _msg.CreateReactionAsync(emoji);
            }
        }

        private static DiscordEmbed CreateMenuEmbed(DiscordGuild _guild)
        {
            // Build the Description
            StringBuilder sb = new StringBuilder();

            sb.AppendLine(Program.Config.RoleMenu.RoleMenuDescription);

            // Build the Roles and Emojis
            foreach (DiscordEmoji emoji in StorageContext.Model.RoleMenu.GetAllEmojis())
            {
                // Make sure the Role exists then add it onto the description
                if (!StorageContext.Model.RoleMenu.GetRoleID(emoji, out ulong _roleID)) continue;
                DiscordRole role = _guild.GetRole(_roleID);
                sb.AppendLine($"{emoji} - {role.Name}");
            }

            // Wrap it up in a nice Embed
            DiscordEmbedBuilder embedBuilder = new DiscordEmbedBuilder()
            {
                Color = new DiscordColor("#5c89fb"),
                Title = Program.Config.RoleMenu.RoleMenuTitle,
                Description = sb.ToString()
            };

            return embedBuilder.Build();
        }

        public static async Task Initialize(DiscordClient _client)
        {
            // Initialize defaults if the configuration and data don't exist
            if (StorageContext.Model.RoleMenu == null)
            {
                StorageContext.Model.RoleMenu = new Storage.Models.RoleMenuModel();
                StorageContext.Model.RoleMenu.Roles = new List<Storage.Models.RoleMenuModel.RoleBind>();
            }
            else
            {
                // If the config exists then check if anyone reacted while the bot was offline
                if (StorageContext.Model.RoleMenu.RoleMenuChannelID != 0)
                {
                    // Get the Role Menu Message
                    DiscordChannel channel = await _client.GetChannelAsync(StorageContext.Model.RoleMenu.RoleMenuChannelID);
                    DiscordMessage msg = await channel.GetMessageAsync(StorageContext.Model.RoleMenu.RoleMenuMsgID);

                    if (msg.Channel == null)
                    {
                        // I don't know why. I shouldn't have to wonder why.
                        // But for some reason randomly the GetMessageAsync
                        // returns a message that doesn't have a channel
                        // So we have to skip checking for reactions
                        _client.MessageReactionAdded += OnReacted;
                        return;
                    }

                    // Get all reactions
                    foreach (DiscordReaction reaction in msg.Reactions)
                    {
                        if (reaction.Count == 1) continue;
                        if (StorageContext.Model.RoleMenu.GetRoleID(reaction.Emoji, out ulong _roleID))
                        {
                            IReadOnlyList<DiscordUser> users = await msg.GetReactionsAsync(reaction.Emoji);

                            // Get users who reacted with the emoji
                            foreach (DiscordUser user in users)
                            {
                                if (user.IsBot) continue;

                                // Grant role to user
                                DiscordMember member = await channel.Guild.GetMemberAsync(user.Id);
                                await member.GrantRoleAsync(member.Guild.GetRole(_roleID));
                            }
                        }
                    }

                    // In case someone reacted with an emoji outside of our list
                    // refresh the message to remove it
                    await UpdateRoleMenu(msg);
                }
            }

            // Listen to reactions on the Role Menu
            _client.MessageReactionAdded += OnReacted;
        }
    }
}