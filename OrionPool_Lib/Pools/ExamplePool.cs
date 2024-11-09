﻿using NLog;
using Org.BouncyCastle.Asn1;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using Solnet.Rpc;
using Solnet.Wallet;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace OrionClientLib.Pools
{
    //UI lib: https://spectreconsole.net/
    public class ExamplePool : IPool
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public string PoolName { get; private set; } = "Example Pool";

        //Name displayed
        public string DisplayName => $"[b]{PoolName}[/]";

        public string Description { get; private set; } = "Example pool sample";

        //Features displayed on pool list
        //Currently not displayed anywhere
        public Dictionary<string, string> Features => new Dictionary<string, string>
        {
            { "Feature 1 example", "Example feature description"},
            { "Feature 2 example", "[red]Another description[/]"}
        };

        public bool HideOnPoolList => false;

        public event EventHandler<NewChallengeInfo> OnChallengeUpdate;
        public event EventHandler<string[]> OnMinerUpdate;
        public event EventHandler PauseMining;
        public event EventHandler ResumeMining;

        private IRpcClient _rpcClient = ClientFactory.GetClient(Cluster.MainNet);
        //private IRpcClient _rpcClient = ClientFactory.GetClient(RPC_URL);

        //Streaming RPC client
        //private IStreamingRpcClient _rpcStreamingClient = ClientFactory.GetStreamingClient(Cluster.MainNet);

        private System.Timers.Timer _sw;
        private int _challengeId = 0;
        private DifficultyInfo _bestDifficulty;
        private ExamplePoolSettings _settings;

        public string[] TableHeaders()
        {
            //Headers for a table of information the pool can provide
            //Return null to disable table
            return ["Time", "Difficulty", "Ore Reward"];
        }

        public void SetWalletInfo(Wallet wallet, string publicKey)
        {
            //This can be called multiple times

            _settings ??= new ExamplePoolSettings(PoolName);

            //wallet is when a private key is needed. This can be null
            //publicKey is when only the public key is needed
        }

        public async Task<bool> ConnectAsync(CancellationToken token)
        {
            //Handle any connection logic to the pool


            //Load current settings, if they exist
            await _settings.LoadAsync();

            //As an example, set a new random challenge every 30s
            _sw = new System.Timers.Timer(30000);
            _sw.Elapsed += (sender, data) =>
            {
                //Array item length must be the same as the table header length
                OnMinerUpdate?.Invoke(this, 
                    [
                        DateTime.Now.ToShortTimeString(), 
                        _bestDifficulty?.BestDifficulty.ToString() ?? "0", 
                        (RandomNumberGenerator.GetInt32(0, 1000000000) / 1000000000.0).ToString()
                    ]);

                _bestDifficulty = null;
                SetRandomChallenge();
            };
            _sw.Start();

            SetRandomChallenge();

            return true;
        }

        public void DifficultyFound(DifficultyInfo info)
        {
            //Gets called whenever a hasher finds a higher difficulty for current challenge

            //Wrong id, ignore solution
            if(info.ChallengeId != _challengeId)
            {
                return;
            }

            if(_bestDifficulty == null || _bestDifficulty.BestDifficulty < info.BestDifficulty)
            {
                _bestDifficulty = info;
            }

            return;
        }

        public async Task<bool> DisconnectAsync()
        {
            //Handle any disconnection logic to the pool

            _sw?.Dispose();
            return true;
        }

        public async Task<double> GetFeeAsync(CancellationToken token)
        {
            //Returns current pool fee
            return 0;
        }

        public async Task<bool> SetupAsync(CancellationToken token, bool initialSetup = false)
        {
            //Called when someone selects the pool through the "Run Setup" menu or selects "Start Mining"

            //initialSetup is true when pool is selected through "Run Setup". Allowing user to change already set options is best

            //Handle any setup required
            //Can be things like verifying registered status, ata creationg, etc

            //Load current saved settings
            await _settings.LoadAsync();

            //An example of doing a setup for pool settings. Only allow changing when the value is empty or through initial setup
            if (String.IsNullOrEmpty(_settings.ExampleSetting) || initialSetup)
            {
                TextPrompt<string> example = new TextPrompt<string>("Just an example. Type anything: ");
                example.DefaultValue(_settings.ExampleSetting);
                _settings.ExampleSetting = await example.ShowAsync(AnsiConsole.Console, token);
                await _settings.SaveAsync();

                //Text prompt doesn't clear the console
                AnsiConsole.Clear();
            }

            return true;
        }

        public async Task OptionsAsync(CancellationToken token)
        {
            //This is called when the user clicks the pool in the initial menu
            //You can use this to setup custom calls (staking, balance, claim, etc)

            SelectionPrompt<string> selectionPrompt = new SelectionPrompt<string>();
            selectionPrompt.Title("Example title");

            for(int i = 0; i < 5; i++)
            {
                selectionPrompt.AddChoice($"Choice {i}");
            }

            //Use cancellation token given as it will allow the user to go back to main menu with Ctrl+C
            string choice = await selectionPrompt.ShowAsync(AnsiConsole.Console, token);
        }

        private void SetRandomChallenge()
        {
            byte[] challenge = new byte[32];
            RandomNumberGenerator.Fill(challenge);

            OnChallengeUpdate?.Invoke(this, new NewChallengeInfo
            {
                Challenge = challenge,
                ChallengeId = Interlocked.Increment(ref _challengeId),
                StartNonce = 0,
                EndNonce = ulong.MaxValue
            });
        }

        private class ExamplePoolSettings : PoolSettings
        {
            public ExamplePoolSettings(string poolName) : base(poolName)
            {
            }

            public string ExampleSetting { get; set; }
        }
    }
}
