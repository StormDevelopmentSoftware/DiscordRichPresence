﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Tasks;
using DiscordRichPresence.Pipe.Entities;
using DiscordRichPresence.Pipe.EventArgs;
using Newtonsoft.Json.Linq;

namespace DiscordRichPresence.Pipe
{
    public delegate Task DiscordCommandCallback(DiscordPipeClient connection, DiscordCommand response);

    public class DiscordPipeClient
    {
        public event Func<Task> Connected;
        public event Func<Exception, Task> Errored;
        public event Func<Task> Disconnected;
        public event Func<ReadyEventArgs, Task> Ready;

        public int RpcVersion { get; internal set; }
        public DiscordUser CurrentUser { get; internal set; }
        public DiscordConfig Environment { get; internal set; }

        private NamedPipeClientStream Pipe;
        private volatile bool IsDisposed;
        private ConcurrentDictionary<string, DiscordCommandCallback> Callbacks;

        internal ulong ApplicationId { get; set; }
        public int InstanceId { internal get; set; }

        public DiscordPipeClient(int instance_id, ulong application_id)
        {
            this.Callbacks = new ConcurrentDictionary<string, DiscordCommandCallback>();

            if (application_id == 0)
                throw new ArgumentNullException(nameof(application_id), "Invalid application id.");

            this.ApplicationId = application_id;

            if (instance_id < 0 || instance_id > 9)
                throw new ArgumentNullException(nameof(instance_id), "Discord instace id, must be in valid range: 0-9");

            this.InstanceId = instance_id;
            this.Pipe = new NamedPipeClientStream(".", $"discord-ipc-{this.InstanceId}", PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        }

        public async Task ConnectAsync()
        {
            try
            {
                await this.Pipe.ConnectAsync().ConfigureAwait(false);
                await Task.Delay(2500).ConfigureAwait(false);

                if (!this.Pipe.IsConnected)
                    throw new InvalidOperationException("Pipe is not connected.");

                _ = Task.Run(async () =>
                {
                    await this.SendAsync(DiscordFrameType.Handshake, new DiscordHandshake { ClientId = this.ApplicationId }).ConfigureAwait(false);
                    await this.ReadPipeAsync();
                });

                this.Connected?.Invoke();
            }
            catch (Exception ex)
            {
                await this.Errored?.Invoke(ex);
                await this.DisconnectAsync().ConfigureAwait(false);
            }
        }

        public async Task DisconnectAsync()
        {
            if (this.IsDisposed)
                return;

            this.IsDisposed = true;
            this.Pipe.Dispose();
            this.Pipe = null;

            await this.Disconnected?.Invoke();
        }

        async Task RequireConnectedAsync()
        {
            if (!this.Pipe.IsConnected)
                await this.ConnectAsync();
        }

        async Task ReadPipeAsync()
        {
            while (this.Pipe != null && this.Pipe.IsConnected)
            {
                try
                {
                    var raw = new byte[this.Pipe.InBufferSize];

                    if (await this.Pipe.ReadAsync(raw, 0, raw.Length).ConfigureAwait(false) > 0)
                    {
                        var frame = new DiscordFrame(raw);

                        Debug.WriteLine("[DISCORD-IPC] ReadPipeAsync(): <<: {0}", args: frame.GetJson());

                        switch (frame.Type)
                        {
                            case DiscordFrameType.Frame:
                                await this.HandleFrameAsync(frame);
                                break;

                            case DiscordFrameType.Close:
                                await this.DisconnectAsync();
                                break;
                        }
                    }

                    await Task.Delay(1);
                }
                catch (Exception ex)
                {
                    await this.Errored?.Invoke(ex);
                    await this.DisconnectAsync().ConfigureAwait(false);
                }
            }

            await this.DisconnectAsync().ConfigureAwait(false);
        }

        internal async Task SendAsync(DiscordFrameType type, object payload)
        {
            if (this.Pipe == null || !this.Pipe.IsConnected)
                return;

            var result = new DiscordFrame()
                .WithType(type)
                .WithPayload(payload)
                .GetBytes();

            await this.RequireConnectedAsync().ConfigureAwait(false);
            await this.Pipe.WriteAsync(result, 0, result.Length).ConfigureAwait(false);
        }

        internal async Task SendCommandAsync(DiscordFrameType type, DiscordCommand command, DiscordCommandCallback callback = null)
        {
            if (this.Pipe == null || !this.Pipe.IsConnected)
                return;

            var result = new DiscordFrame()
                .WithType(type)
                .WithPayload(command)
                .GetBytes();

            if (callback != null)
                this.Callbacks.AddOrUpdate(command.Nonce, callback, (key, old) => callback);

            await this.RequireConnectedAsync().ConfigureAwait(false);
            await this.Pipe.WriteAsync(result, 0, result.Length).ConfigureAwait(false);
        }

        internal async Task HandleFrameAsync(DiscordFrame frame)
        {
            var payload = frame.Payload.ToObject<DiscordCommand>();

            if (!string.IsNullOrEmpty(payload.Nonce))
            {
                if (this.Callbacks.TryRemove(payload.Nonce, out var callback))
                {
                    _ = Task.Run(() => callback(this, payload).ConfigureAwait(false));
                    return;
                }
            }

            switch (payload.Command)
            {
                case DiscordCommandType.Dispatch:
                    await this.HandleEventAsync(frame, payload).ConfigureAwait(false);
                    break;
            }
        }

        public Task SetActivityAsync(Action<DiscordActivity> activity)
        {
            var model = new DiscordActivity();
            activity(model);
            return this.SetActivityAsync(model, null);
        }

        public async Task SetActivityAsync(DiscordActivity activity, int? pid = null)
        {
            var command = new DiscordCommand()
            {
                Command = DiscordCommandType.SetActivity,
                Arguments = JObject.FromObject(new
                {
                    pid = pid.GetValueOrDefault(Process.GetCurrentProcess().Id),
                    activity
                })
            };

            var result = new DiscordFrame()
                .WithType(DiscordFrameType.Frame)
                .WithPayload(command)
                .GetBytes();

            await this.RequireConnectedAsync().ConfigureAwait(false);
            await this.Pipe.WriteAsync(result, 0, result.Length).ConfigureAwait(false);
        }

        protected async Task HandleEventAsync(DiscordFrame frame, DiscordCommand command)
        {
            switch (command.Event)
            {
                case DiscordEventType.Ready:
                    {
                        var e = command.Data.ToObject<ReadyEventArgs>();
                        e.Client = this;

                        this.Environment = e.Configuration;
                        this.RpcVersion = e.Version;
                        this.CurrentUser = e.User;

                        var handler = this.Ready;

                        if (handler != null)
                            await handler.Invoke(e).ConfigureAwait(false);
                    }
                    break;
            }
        }
    }
}