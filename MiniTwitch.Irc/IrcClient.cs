﻿using Microsoft.Extensions.Logging;
using MiniTwitch.Common;
using MiniTwitch.Common.Extensions;
using MiniTwitch.Irc.Enums;
using MiniTwitch.Irc.Interfaces;
using MiniTwitch.Irc.Internal;
using MiniTwitch.Irc.Internal.Enums;
using MiniTwitch.Irc.Internal.Parsing;
using MiniTwitch.Irc.Models;

namespace MiniTwitch.Irc;

/// <summary>
/// Responsible for all communication with Twitch IRC. Parses and invokes events for IRC messages
/// </summary>
public sealed class IrcClient : IAsyncDisposable
{
    #region Properties
    /// <summary>
    /// The action to invoke when an exception is caught within an event
    /// </summary>
    public Action<Exception> ExceptionHandler { get; set; } = default!;
    /// <summary>
    /// List of the currently joined channels
    /// <para>Note: The client will attempt to rejoin all channels upon reconnection</para>
    /// </summary>
    public List<IBasicChannel> JoinedChannels { get; } = new();
    /// <summary>
    /// Time to wait before attempting to reconnect to TMI upon disconnection
    /// <para>Do not use this property to change the reconnection delay. Change <see cref="ClientOptions.ReconnectionDelay"/> in the constructor instead</para>
    /// </summary>
    [Obsolete("Changing this value does nothing; It will be removed in the future")]
    public TimeSpan ReconnectionDelay { get; init; } = TimeSpan.FromSeconds(30);

    internal ClientOptions Options { get; init; }
    #endregion

    #region Events
    /// <summary>
    /// Invoked upon connecting to TMI
    /// <para>Note: This is only invoked once. Following connections to TMI will invoke <see cref="OnReconnect"/></para>
    /// </summary>
    public event Func<ValueTask> OnConnect = default!;
    /// <summary>
    /// Invoked upon reconnecting to TMI
    /// </summary>
    public event Func<ValueTask> OnReconnect = default!;
    /// <summary>
    /// Invoked upon disconnection from TMI
    /// </summary>
    public event Func<ValueTask> OnDisconnect = default!;
    /// <summary>
    /// Invoked when a message is received
    /// </summary>
    public event Func<Privmsg, ValueTask> OnMessage = default!;
    /// <summary>
    /// Invoked when a user is about to gift subscription(s)
    /// <para>Provides a count of how many they are gifting (<see cref="IGiftSubNoticeIntro.GiftCount"/>), and how much they have gifted in total (<see cref="IGiftSubNoticeIntro.TotalGiftCount"/>)</para>
    /// </summary>
    public event Func<IGiftSubNoticeIntro, ValueTask> OnGiftedSubNoticeIntro = default!;
    /// <summary>
    /// Invoked when a user is gifted a subscription
    /// </summary>
    public event Func<IGiftSubNotice, ValueTask> OnGiftedSubNotice = default!;
    /// <summary>
    /// Invoked when a user (re)subscribes
    /// </summary>
    public event Func<ISubNotice, ValueTask> OnSubscriptionNotice = default!;
    /// <summary>
    /// Invoked when a raid occurs
    /// </summary>
    public event Func<IRaidNotice, ValueTask> OnRaidNotice = default!;
    /// <summary>
    /// Invoked when a user continues their gifted subscription
    /// <para>Note: <see cref="IPaidUpgradeNotice.GifterUsername"/> and <see cref="IPaidUpgradeNotice.GifterDisplayName"/> will equal to <see cref="string.Empty"/> if the gifted subscription was from an anonymous gifter</para>
    /// </summary>
    public event Func<IPaidUpgradeNotice, ValueTask> OnPaidUpgradeNotice = default!;
    /// <summary>
    /// Invoked when a user converts their subscription plan from Prime to paid (Tier 1, 2 or 3)
    /// </summary>
    public event Func<IPrimeUpgradeNotice, ValueTask> OnPrimeUpgradeNotice = default!;
    /// <summary>
    /// Invoked when a chat announcement is sent
    /// </summary>
    public event Func<IAnnouncementNotice, ValueTask> OnAnnouncement = default!;
    /// <summary>
    /// Invoked when a user gets timed out
    /// </summary>
    public event Func<IUserTimeout, ValueTask> OnUserTimeout = default!;
    /// <summary>
    /// Invoked when a user gets banned
    /// </summary>
    public event Func<IUserBan, ValueTask> OnUserBan = default!;
    /// <summary>
    /// Invoked when a chat gets cleared
    /// </summary>
    public event Func<IChatClear, ValueTask> OnChatClear = default!;
    /// <summary>
    /// Invoked when a message gets deleted
    /// </summary>
    public event Func<Clearmsg, ValueTask> OnMessageDelete = default!;
    /// <summary>
    /// Invoked when a channel is joined
    /// <para>Contains ROOMSTATE information such as <see cref="IrcChannel.FollowerModeEnabled"/>, <see cref="IrcChannel.SubOnlyEnabled"/>, <see cref="IrcChannel.SlowModeDuration"/>...</para>
    /// </summary>
    public event Func<IrcChannel, ValueTask> OnChannelJoin = default!;
    /// <summary>
    /// Invoked when "Emote Only" mode is either activated or deactivated
    /// </summary>
    public event Func<IEmoteOnlyModified, ValueTask> OnEmoteOnlyModified = default!;
    /// <summary>
    /// Invoked when "Followers Only" mode is either activated, modified or deactivated
    /// </summary>
    public event Func<IFollowersOnlyModified, ValueTask> OnFollowerModeModified = default!;
    /// <summary>
    /// Invoked when "R9K"/"Unique" mode is either activated or deactivated
    /// </summary>
    public event Func<IR9KModified, ValueTask> OnUniqueModeModified = default!;
    /// <summary>
    /// Invoked when "Slow" mode is either activated, modified or deactivated
    /// </summary>
    public event Func<ISlowModeModified, ValueTask> OnSlowModeModified = default!;
    /// <summary>
    /// Invoked when "Subscribers Only" mode is either activated or deactivated
    /// </summary>
    public event Func<ISubOnlyModified, ValueTask> OnSubOnlyModified = default!;
    /// <summary>
    /// Invoked when a channel is parted
    /// </summary>
    public event Func<IPartedChannel, ValueTask> OnChannelPart = default!;
    /// <summary>
    /// Invoked when a NOTICE is received
    /// </summary>
    public event Func<Notice, ValueTask> OnNotice = default!;
    /// <summary>
    /// Invoked when a USERSTATE is received
    /// <para>USERSTATEs are received upon initially joining a channel or sending a message</para>
    /// <para> They provide information about you (<seealso cref="Userstate.Self"/>), and your available emote sets (<see cref="Userstate.EmoteSets"/>) in that channel</para>
    /// </summary>
    public event Func<Userstate, ValueTask> OnUserstate = default!;
    /// <summary>
    /// Invoked when a whisper Is received
    /// </summary>
    public event Func<Whisper, ValueTask> OnWhisper = default!;

    internal event Func<ValueTask> OnPing = default!;
    #endregion

    #region Fields
    private readonly SemaphoreSlim _connectionWaiter = new(0);
    private readonly SemaphoreSlim _joinChannelWaiter = new(0);
    private readonly List<string> _moderated = new();
    private readonly RateLimitManager _manager;
    private readonly WebSocketClient _ws;
    private Uri _targetUrl = default!;
    private string _loggingHeader = "[MiniTwitch:Irc-default!]";
    private bool _connectInvoked;
    #endregion

    #region Init
    /// <summary>
    /// Creates a new instance of <see cref="IrcClient"/>
    /// </summary>
    public IrcClient(Action<ClientOptions> options)
    {
        ClientOptions clientOptions = new();
        options.Invoke(clientOptions);
        this.Options = clientOptions;
        _ws = new WebSocketClient(this.Options.ReconnectionDelay, 2048);
        _manager = new(clientOptions);

        InternalInit();
    }

    private void InternalInit()
    {
        this.Options.CheckCredentials();

        this.ExceptionHandler ??= LogEventException;
        _loggingHeader = "[MiniTwitch:Irc-Anonymous]";
        if (!this.Options.Anonymous)
        {
            this.Options.OAuth = Utils.CheckToken(this.Options.OAuth);
            _loggingHeader = $"[MiniTwitch:Irc-{this.Options.Username}]";
        }

        OnPing += Ping;
        _ws.OnDisconnect += OnWsDisconnect;
        _ws.OnReconnect += OnWsReconnect;
        _ws.OnConnect += Login;
        _ws.OnData += Parse;
        _ws.OnLog += Log;
        _ws.OnLogEx += LogException;
    }
    #endregion

    #region Connection
    private async Task Login()
    {
        await _ws.SendAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
        if (this.Options.Anonymous)
        {
            await _ws.SendAsync($"NICK justinfan{Random.Shared.Next(100, 900)}");
            return;
        }

        await _ws.SendAsync($"PASS oauth:{this.Options.OAuth}", this.Options.HideAuthenticationLogs);
        await _ws.SendAsync($"NICK {this.Options.Username}", this.Options.HideAuthenticationLogs);
    }

    /// <summary>
    /// Attempts connection to TMI like <see cref="ConnectAsync(string, CancellationToken)"/>, but connects in a "fire and forget" style
    /// </summary>
    public void Connect(string url = "wss://irc-ws.chat.twitch.tv:443") => ConnectAsync(url).StepOver();

    /// <summary>
    /// Connects to TMI
    /// </summary>
    /// <returns><see langword="true"/> if the connection is successful; Otherwise, after 15 seconds: <see langword="false"/></returns>
    public async Task<bool> ConnectAsync(string url = "wss://irc-ws.chat.twitch.tv:443",
        CancellationToken cancellationToken = default)
    {
        _targetUrl = new(url);
        await _ws.Start(_targetUrl, cancellationToken);
        if (await _connectionWaiter.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
            return true;

        Log(LogLevel.Critical, "Connection timed out.");
        return false;
    }

    /// <summary>
    /// Disconnects from TMI in a "fire and forget" style
    /// </summary>
    public void Disconnect() => _ws.Disconnect().StepOver();

    /// <summary>
    /// Disconnects from TMI
    /// </summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _ws.Disconnect(cancellationToken);

    /// <summary>
    /// Disconnects then reconnects to TMI
    /// </summary>
    public Task ReconnectAsync(CancellationToken cancellationToken = default) => _ws.Restart(this.Options.ReconnectionDelay, cancellationToken);

    private async Task OnWsReconnect()
    {
        await Login();
        foreach (string channel in this.JoinedChannels.Select(c => c.Name))
        {
            bool res = await JoinChannel(channel);
            Log(LogLevel.Information, $"{(res ? "Rejoined" : "Failed to rejoin")} channel: {{channel}}", channel);
            await Task.Delay(1000);
        }
    }

    private Task OnWsDisconnect()
    {
        OnDisconnect?.Invoke().StepOver(this.ExceptionHandler);
        return Task.CompletedTask;
    }
    #endregion

    #region Communication
    /// <summary>
    /// Send a raw message to TMI
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    public async ValueTask SendRaw(string message, CancellationToken cancellationToken = default)
    {
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to send raw message {message}: Not connected.", message);
            return;
        }

        await _ws.SendAsync(message, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sends a chat message
    /// </summary>
    /// <param name="channel">The channel to send the message to</param>
    /// <param name="message">The message to send</param>
    /// <param name="action">Whether to prepend .me to the message</param>
    /// <param name="nonce">Custom nonce to send with the message. Value can't contain spaces</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    public async ValueTask SendMessage(string channel, string message, bool action = false, string? nonce = null, CancellationToken cancellationToken = default)
    {
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to send message {message}: Not connected.", message);
            return;
        }
        else if (this.Options.Anonymous)
        {
            Log(LogLevel.Error, "Failed to send message {message}: Cannot send message with anonymous account.", message);
            return;
        }

        if (nonce is not null && nonce.Contains(' '))
        {
            Log(LogLevel.Error, "Failed to send message {message}: Nonce cannot contain spaces.", message);
            return;
        }

        if (!_manager.CanSend(channel, _moderated.Contains(channel)))
        {
            await Task.Delay(2500, cancellationToken);
            await SendMessage(channel, message, action, nonce, cancellationToken);
            Log(LogLevel.Debug, "Cannot send message to #{channel}: Rate limit of {count} hit. Retrying in {delay}ms", channel, this.Options.ModMessageRateLimit, 2500);
            Log(LogLevel.Warning, "#{channel}: Your message was not sent yet due to the configured messaging ratelimit (normal: {normal}/30s, mod: {mod}/30s)",
                channel, this.Options.MessageRateLimit, this.Options.ModMessageRateLimit);
            return;
        }

        string outMsg = $"{(nonce is null ? string.Empty : $"@client-nonce={nonce}")} PRIVMSG #{channel} :{(action ? $".me {message}" : message)}";

        await _ws.SendAsync(outMsg, cancellationToken: cancellationToken);
    }
    /// <summary>
    /// Replies to a chat message
    /// </summary>
    /// <param name="parentMessage">The message to reply to</param>
    /// <param name="message">The message to reply with</param>
    /// <param name="action">Prepend .me to the message</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    public async ValueTask ReplyTo(Privmsg parentMessage, string message, bool action = false, CancellationToken cancellationToken = default)
    {
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to send reply {message}: Not connected.", message);
            return;
        }
        else if (this.Options.Anonymous)
        {
            Log(LogLevel.Error, "Failed to send reply {message}: Cannot send message with anonymous account.", message);
            return;
        }

        string channel = parentMessage.Channel.Name;
        if (!_manager.CanSend(channel, _moderated.Contains(channel)))
        {
            await Task.Delay(2500, cancellationToken);
            await ReplyTo(parentMessage, message, action, cancellationToken);
            Log(LogLevel.Debug, "Cannot send message to #{channel}: Rate limit of {count} hit. Retrying in {delay}ms", channel, this.Options.ModMessageRateLimit, 2500);
            Log(LogLevel.Warning, "#{channel}: Your message was not sent yet due to the configured messaging ratelimit (normal: {normal}/30s, mod: {mod}/30s)",
                channel, this.Options.MessageRateLimit, this.Options.ModMessageRateLimit);
            return;
        }

        string outMsg = $"@reply-parent-msg-id={parentMessage.Id} PRIVMSG #{channel} :{(action ? $".me {message}" : message)}";

        await _ws.SendAsync(outMsg, cancellationToken: cancellationToken);
    }
    /// <summary>
    /// Replies to a chat message
    /// </summary>
    /// <param name="messageId">The ID of the message to reply to</param>
    /// <param name="channel">the channel in which that message was sent</param>
    /// <param name="reply">The message to reply with</param>
    /// <param name="action">Prepend .me to the message</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    /// <returns></returns>
    public async ValueTask ReplyTo(string messageId, string channel, string reply, bool action = false, CancellationToken cancellationToken = default)
    {
        const string privmsg = "PRIVMSG #";
        const string replyTag = "@reply-parent-msg-id=";
        const string act = ".me ";
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to send reply {message}: Not connected.", messageId);
            return;
        }
        else if (this.Options.Anonymous)
        {
            Log(LogLevel.Error, "Failed to send reply {message}: Cannot send message with anonymous account.", messageId);
            return;
        }

        if (!_manager.CanSend(channel, _moderated.Contains(channel)))
        {
            await Task.Delay(2500, cancellationToken);
            await ReplyTo(messageId, channel, reply, action, cancellationToken);
            Log(LogLevel.Debug, "Cannot send message to #{channel}: Rate limit of {count} hit. Retrying in {delay}ms", channel, this.Options.ModMessageRateLimit, 2500);
            Log(LogLevel.Warning, "#{channel}: Your message was not sent yet due to the configured messaging ratelimit (normal: {normal}/30s, mod: {mod}/30s)",
                channel, this.Options.MessageRateLimit, this.Options.ModMessageRateLimit);
            return;
        }

        string outMsg = $"{replyTag}{messageId} {privmsg}{channel} :{(action ? $"{act} {reply}" : reply)}";

        await _ws.SendAsync(outMsg, cancellationToken: cancellationToken);
    }

    private async ValueTask Ping()
    {
        Log(LogLevel.Debug, "PING received");
        await _ws.SendAsync("PONG");
    }
    #endregion

    #region Channels
    /// <summary>
    /// Used for joining a channel
    /// </summary>
    /// <param name="channel">Username of the channel to join</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    /// <returns><see langword="true"/> if the join is successful; Otherwise, after 10 seconds: <see langword="false"/></returns>
    public async Task<bool> JoinChannel(string channel, CancellationToken cancellationToken = default)
    {
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to join channel #{channel}:  Not connected.", channel);
            return false;
        }

        if (!_manager.CanJoin())
        {
            await Task.Delay(1000, cancellationToken);
            Log(LogLevel.Warning, "Waiting to join #{channel}: Configured ratelimit of {rate} joins/10s is hit", channel, this.Options.JoinRateLimit);
            return await JoinChannel(channel, cancellationToken);
        }

        await _ws.SendAsync($"JOIN #{channel}", cancellationToken: cancellationToken);
        return await _joinChannelWaiter.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    /// <summary>
    /// Used for joining multiple channels
    /// </summary>
    /// <param name="channels">Usernames of channels to join</param>
    /// /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    /// <returns><see langword="true"/> if all joins are successful; Otherwise, <see langword="false"/></returns>
    public async Task<bool> JoinChannels(IEnumerable<string> channels, CancellationToken cancellationToken = default)
    {
        // TODO: add a non-params JoinChannels method to support CancellationToken passing
        // params isn't ideal when you're potentially receiving a list from an external store - not hardcoding
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to join channels {channels}:  Not connected.", string.Join(',', channels));
            return false;
        }

        bool allSuccess = true;
        foreach (string channel in channels)
        {
            if (!await JoinChannel(channel, cancellationToken))
                allSuccess = false;
        }

        return allSuccess;
    }

    /// <summary>
    /// Used for leaving/parting a joined channel
    /// </summary>
    /// <param name="channel">name of the channel to part</param>
    /// <param name="cancellationToken">A cancellation token to stop further execution of asynchronous actions</param>
    public Task PartChannel(string channel, CancellationToken cancellationToken = default)
    {
        if (!_ws.IsConnected)
        {
            Log(LogLevel.Error, "Failed to part channel {channel}: Not connected.", channel);
            return Task.CompletedTask;
        }

        return _ws.SendAsync($"PART #{channel}", cancellationToken: cancellationToken).AsTask();
    }
    #endregion

    #region Parsing
    internal void Parse(ReadOnlyMemory<byte> data)
    {
        (IrcCommand command, int lfIndex) = IrcParsing.ParseCommand(data.Span);
        int accumulatedIndex = lfIndex;
        ReceiveData(command, data[..(lfIndex == 0 ? ^0 : lfIndex - 2)]);
        // Go over data if it has multiple messages
        while (lfIndex != 0 && data.Length - accumulatedIndex > 0)
        {
            (command, lfIndex) = IrcParsing.ParseCommand(data.Span[accumulatedIndex..]);
            ReceiveData(command, data[accumulatedIndex..(lfIndex == 0 ? ^0 : lfIndex + accumulatedIndex - 2)]);
            accumulatedIndex += lfIndex;
        }
    }

    private void ReceiveData(IrcCommand command, ReadOnlyMemory<byte> data)
    {
        // Return if user decides to ignore the command
        if (this.Options.IgnoreCommands != IgnoreCommand.None && command switch
        {
            IrcCommand.PRIVMSG => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.PRIVMSG),
            IrcCommand.USERNOTICE => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.USERNOTICE),
            IrcCommand.CLEARCHAT => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.CLEARCHAT),
            IrcCommand.CLEARMSG => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.CLEARMSG),
            IrcCommand.WHISPER => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.WHISPER),
            IrcCommand.USERSTATE => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.USERSTATE),
            IrcCommand.JOIN => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.JOIN),
            IrcCommand.PART => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.PART),
            IrcCommand.NOTICE => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.NOTICE),
            IrcCommand.ROOMSTATE => this.Options.IgnoreCommands.HasFlag(IgnoreCommand.ROOMSTATE),
            _ => false
        })
        {
            return;
        }

        switch (command)
        {
            case IrcCommand.PRIVMSG:
                Privmsg ircMessage = new(data, this);
                OnMessage?.Invoke(ircMessage).StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.Connected:
                if (_connectionWaiter.CurrentCount == 0)
                    _ = _connectionWaiter.Release();

                if (_connectInvoked)
                {
                    OnReconnect?.Invoke().StepOver(this.ExceptionHandler);
                    break;
                }

                _connectInvoked = true;
                OnConnect?.Invoke().StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.RECONNECT:
                Log(LogLevel.Information, "Twitch servers requested a reconnection. Reconnecting ...");
                _ws.Restart(this.Options.ReconnectionDelay).StepOver();
                OnReconnect?.Invoke().StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.PING:
                OnPing?.Invoke().StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.USERNOTICE:
                Usernotice usernotice = new(data);
                switch (usernotice.MsgId)
                {
                    case UsernoticeType.Sub
                    or UsernoticeType.Resub:
                        OnSubscriptionNotice?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.Subgift:
                        OnGiftedSubNotice?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.Raid:
                        OnRaidNotice?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.AnonGiftPaidUpgrade
                    or UsernoticeType.GiftPaidUpgrade:
                        OnPaidUpgradeNotice?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.PrimePaidUpgrade:
                        OnPrimeUpgradeNotice?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.Announcement:
                        OnAnnouncement?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;

                    case UsernoticeType.SubMysteryGift:
                        OnGiftedSubNoticeIntro?.Invoke(usernotice).StepOver(this.ExceptionHandler);
                        break;
                }

                break;

            case IrcCommand.CLEARCHAT:
                Clearchat clearchat = new(data);
                if (clearchat.IsClearChat)
                    OnChatClear?.Invoke(clearchat).StepOver(this.ExceptionHandler);
                else if (clearchat.IsBan)
                    OnUserBan?.Invoke(clearchat).StepOver(this.ExceptionHandler);
                else
                    OnUserTimeout?.Invoke(clearchat).StepOver(this.ExceptionHandler);

                break;

            case IrcCommand.CLEARMSG:
                Clearmsg clearmsg = new(data);
                OnMessageDelete?.Invoke(clearmsg).StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.ROOMSTATE:
                IrcChannel ircChannel = new(data);
                if (ircChannel.Roomstate == RoomstateType.All && _joinChannelWaiter.CurrentCount == 0)
                {
                    _ = _joinChannelWaiter.Release();
                    if (!this.JoinedChannels.Contains(ircChannel))
                    {
                        this.JoinedChannels.Add(ircChannel);
                        Log(LogLevel.Information, "Joined #{channel}", ircChannel.Name);
                        Log(LogLevel.Debug, "Added #{channel} to joined channels list.", ircChannel.Name);
                    }

                    OnChannelJoin?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else if (ircChannel.Roomstate == RoomstateType.EmoteOnly)
                {
                    OnEmoteOnlyModified?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else if (ircChannel.Roomstate == RoomstateType.FollowerOnly)
                {
                    OnFollowerModeModified?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else if (ircChannel.Roomstate == RoomstateType.R9K)
                {
                    OnUniqueModeModified?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else if (ircChannel.Roomstate == RoomstateType.Slow)
                {
                    OnSlowModeModified?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else if (ircChannel.Roomstate == RoomstateType.SubOnly)
                {
                    OnSubOnlyModified?.Invoke(ircChannel).StepOver(this.ExceptionHandler);
                }
                else
                {
                    Log(LogLevel.Warning, "Unknown ROOMSTATE type received.");
                }

                break;

            case IrcCommand.PART:
                IrcChannel channel = new(data);
                if (this.JoinedChannels.Remove(channel))
                    Log(LogLevel.Debug, "Removed #{channel} from joined channels list.", channel.Name);

                OnChannelPart?.Invoke(channel).StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.NOTICE:
                Notice notice = new(data);
                if (notice.Type == NoticeType.Msg_channel_suspended)
                    Log(LogLevel.Error, "Tried joining suspended channel: #{channel}", notice.Channel.Name);
                else if (notice.Type == NoticeType.Bad_auth)
                    Log(LogLevel.Critical, "Authentication failed: {message}", notice.SystemMessage);

                OnNotice?.Invoke(notice).StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.USERSTATE or IrcCommand.GLOBALUSERSTATE:
                Userstate state = new(data);
                if (state.Self.IsMod && !_moderated.Contains(state.Channel.Name))
                    _moderated.Add(state.Channel.Name);

                OnUserstate?.Invoke(state).StepOver(this.ExceptionHandler);
                break;

            case IrcCommand.WHISPER:
                Whisper whisper = new(data);
                OnWhisper?.Invoke(whisper).StepOver(this.ExceptionHandler);
                break;
        }
    }
    #endregion

    #region Utils
    private void LogEventException(Exception ex) => LogException(ex, "🚨 Exception caught in an event:");

    private void Log(LogLevel level, string template, params object[] properties) => this.Options.Logger?.Log(level, $"{_loggingHeader} " + template, properties);

    private void LogException(Exception ex, string template, params object[] properties) => this.Options.Logger?.LogError(ex, $"{_loggingHeader} " + template, properties);
    #endregion

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _ws.DisposeAsync();
        this.JoinedChannels.Clear();
        _connectionWaiter.Dispose();
        _joinChannelWaiter.Dispose();
        _moderated.Clear();
    }
}
