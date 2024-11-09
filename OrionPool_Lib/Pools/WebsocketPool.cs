﻿using NLog;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using Solnet.Wallet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    public abstract class WebsocketPool : IPool
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public abstract string PoolName { get; }
        public abstract string DisplayName { get; }
        public abstract string Description { get; }

        public abstract Dictionary<string, string> Features { get; }

        public abstract bool HideOnPoolList { get; }
        public abstract string HostName { get; }
        public abstract Uri WebsocketUrl { get; }

        public abstract event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public abstract event EventHandler<string[]> OnMinerUpdate;
        public abstract event EventHandler PauseMining;
        public abstract event EventHandler ResumeMining;

        protected ClientWebSocket _webSocket;
        protected Wallet _wallet;
        protected string _publicKey;
        protected string _authorization = null;

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _receiveThread;

        public virtual async Task<bool> ConnectAsync(CancellationToken token)
        {
            _webSocket = new ClientWebSocket();

            if (!String.IsNullOrEmpty(_authorization))
            {
                _webSocket.Options.SetRequestHeader("Authorization", _authorization);
            }

            try
            {
                CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                await _webSocket.ConnectAsync(WebsocketUrl, cts.Token);

                _receiveThread = new Task(ReceiveThread);
                _receiveThread.Start();

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to connect to pool. Url: {WebsocketUrl}. Message: {ex.Message}");
            }

            return false;
        }

        public virtual async Task<bool> DisconnectAsync()
        {
            try
            {
                _cts.Cancel();

                //Give it 1 second to disconnect 
                CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);

                _webSocket?.Dispose();

                return true;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Failed to disconnect from websocket. Url: {WebsocketUrl}");
            }

            return false;
        }

        protected virtual async Task<bool> SendMessageAsync(IMessage message)
        {
            try
            {
                await _webSocket.SendAsync(message.Serialize(), WebSocketMessageType.Binary, true, _cts.Token);

                return true;
            }
            catch (TaskCanceledException)
            {
                return false;
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Warn, $"Failed to send websocket data. Message: {ex.Message}");

                return false;
            }
        }

        public virtual void SetWalletInfo(Wallet wallet, string publicKey)
        {
            if (publicKey != null)
            {
                _wallet = wallet;
                _publicKey = publicKey;
            }
        }

        private async void ReceiveThread()
        {
            byte[] data = new byte[1024];
            ArraySegment<byte> buffer = new ArraySegment<byte>(data);

            int currentCount = 0;

            while (!_cts.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(buffer.Slice(currentCount), _cts.Token);

                    currentCount += result.Count;

                    //Potentially more data, increase overall buffer size
                    if (currentCount == data.Length)
                    {
                        byte[] nData = new byte[data.Length * 2];
                        Array.Copy(data, nData, result.Count);
                        buffer = new ArraySegment<byte>(nData);
                        data = nData;
                    }

                    if(result.EndOfMessage)
                    {
                        OnMessage(buffer.Slice(0, currentCount).ToArray(), result.MessageType);

                        currentCount = 0;
                    }
                }
                catch(TaskCanceledException)
                {
                    //Ignore
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Warn, ex, $"Exception occurred when receiving websocket data. Message: {ex.Message}");
                }
            }

            //Wasn't canceled, so try reconnecting
            if(!_cts.IsCancellationRequested)
            {
                _logger.Log(LogLevel.Warn, $"Websocket disconnected. Trying to reconnecting ...");
                while(!await ConnectAsync(_cts.Token))
                {
                    const int seconds = 5;

                    _logger.Log(LogLevel.Warn, $"Failed to connect. Retrying in {seconds}s");

                    await Task.Delay(seconds * 1000);
                }
            }
        }

        public abstract void DifficultyFound(DifficultyInfo info);
        public abstract Task<double> GetFeeAsync(CancellationToken token);

        public abstract Task<bool> SetupAsync(CancellationToken token, bool initialSetup = false);

        public abstract Task OptionsAsync(CancellationToken token);
        public abstract string[] TableHeaders();
        public abstract void OnMessage(ArraySegment<byte> buffer, WebSocketMessageType type);
    }
}
