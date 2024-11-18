﻿using DrillX.Solver;
using Equix;
using NLog;
using OrionClientLib.Hashers.Models;
using OrionClientLib.Pools.Models;
using OrionClientLib.Pools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ILGPU;
using ILGPU.IR;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime;
using DrillX.Compiler;
using DrillX;
using System.Buffers.Binary;
using Blake2Sharp;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Asn1.Cmp;
using Org.BouncyCastle.Asn1.X509;
using OrionClientLib.Hashers.GPU;
using ILGPU.Runtime.CPU;

namespace OrionClientLib.Hashers
{
    public abstract class BaseGPUHasher : IHasher, IGPUHasher
    {
        protected static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private const int _maxNonces = 2048;
        private const int _maxQueueSize = 2;

        public IHasher.Hardware HardwareType => IHasher.Hardware.GPU;
        public bool Initialized => _taskRunner?.IsCompleted == false;
        public TimeSpan CurrentChallengeTime => _sw.Elapsed - _challengeStartTime;
        public event EventHandler<HashrateInfo> OnHashrateUpdate;
        public abstract string Name { get; }
        public abstract string Description { get; }

        protected Stopwatch _sw = Stopwatch.StartNew();
        protected TimeSpan _challengeStartTime;

        protected bool _running = false;
        protected bool _executing = false;
        protected bool _hasNotice => !_running || ResettingChallenge || !_pauseMining.WaitOne(0);

        protected HasherInfo _info = new HasherInfo();

        protected IPool _pool;
        protected ManualResetEvent _newChallengeWait = new ManualResetEvent(false);
        protected ManualResetEvent _pauseMining = new ManualResetEvent(true);
        public bool IsMiningPaused => !_pauseMining.WaitOne(0);

        protected bool ResettingChallenge => !_newChallengeWait.WaitOne(1);

        private Task _taskRunner;
        private int _threads = Environment.ProcessorCount;
        private List<GPUDeviceHasher> _gpuDevices = new List<GPUDeviceHasher>();
        private List<CPUData> _gpuData = new List<CPUData>();
        private Context _context;

        private BlockingCollection<CPUData> _availableCPUData = new BlockingCollection<CPUData>(new ConcurrentQueue<CPUData>());
        private BlockingCollection<CPUData> _setupCPUData = new BlockingCollection<CPUData>(new ConcurrentQueue<CPUData>());

        private ulong _currentNonce = 0;

        public async Task<bool> InitializeAsync(IPool pool, Settings settings)
        {
            if (Initialized)
            {
                return false;
            }

            if (settings.GPUDevices == null || settings.GPUDevices.Count == 0)
            {
                return false;
            }

            _pool = pool;
            _running = true;
            //Use total CPU threads for now
            _threads = settings.CPUThreads; //TODO: Change to use remaining threads
            _info = new HasherInfo();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate += _pool_OnChallengeUpdate;
            }

            _context = Context.Create(builder => builder.AllAccelerators().Profiling().IOOperations()
            .Inlining(InliningMode.Aggressive)
            .Optimize(OptimizationLevel.O0));

            IntrinsicsLoader.Load(this.GetType(), _context);

            var devices = _context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();

            List<Device> devicesToUse = new List<Device>();
            HashSet<Device> supportedDevices = new HashSet<Device>(GetDevices(true));

            foreach(var d in settings.GPUDevices)
            {
                if(d >= 0 && d < devices.Count)
                {
                    if (supportedDevices.Contains(devices[d]))
                    {
                        devicesToUse.Add(devices[d]);
                    }
                }
            }

            if (devicesToUse.Count == 0)
            {
                _logger.Log(LogLevel.Warn, $"No supported GPU devices selected");

                return false;
            }

            //TODO: Allow additional buffer room
            int maxNonces = (int)((ulong)devicesToUse.Min(x => x.MemorySize) / GPUDeviceHasher.MemoryPerNonce);

            //Reduce to a power of 2
            maxNonces = (int)Math.Pow(2, (int)Math.Log2(maxNonces));

            //Reduce to 4096 being the max
            maxNonces = Math.Min(_maxNonces, maxNonces);

            for(int i = 0; i < devicesToUse.Count; i++)
            {
                var device = devicesToUse[i];

                GPUDeviceHasher dHasher = new GPUDeviceHasher(HashxKernel(), EquihashKernel(), device.CreateAccelerator(_context), 
                                                              device, i, _setupCPUData, _availableCPUData, _info,
                                                              GetHashXKernelConfig(device, maxNonces), GetEquihashKernelConfig(device, maxNonces));

                dHasher.OnDifficultyUpdate += DHasher_OnDifficultyUpdate;
                dHasher.OnHashrateUpdate += DHasher_OnHashrateUpdate;
                dHasher.Initialize(maxNonces);

                _gpuDevices.Add(dHasher);
            }

            for(int i =0; i < devicesToUse.Count * _maxQueueSize * 2; i++)
            {
                _availableCPUData.Add(new CPUData(maxNonces));
            }

            //Start CPU program generation thread
            _taskRunner = new Task(RunProgramGeneration, TaskCreationOptions.LongRunning);
            _taskRunner.Start();

            return true;
        }

        private void DHasher_OnHashrateUpdate(object? sender, HashrateInfo e)
        {
            e.TotalTime = _sw.Elapsed - _challengeStartTime;

            OnHashrateUpdate?.Invoke(this, e);
        }

        private void DHasher_OnDifficultyUpdate(object? sender, EventArgs e)
        {
            _pool?.DifficultyFound(_info.DifficultyInfo.GetUpdateCopy());
        }

        private async void RunProgramGeneration()
        {
            try
            {
                while (_running)
                {
                    _executing = false;

                    CPUData cpuData = null;

                    while ((!_newChallengeWait.WaitOne(500) || !_pauseMining.WaitOne(0)) && _running)
                    {

                    }

                    if (!_running)
                    {
                        continue;
                    }

                    _executing = true;

                    while (!_hasNotice && !_availableCPUData.TryTake(out cpuData, 50))
                    {

                    }

                    if (_hasNotice)
                    {
                        if (cpuData != null)
                        {
                            _availableCPUData.TryAdd(cpuData);
                        }

                        continue;
                    }

                    #region Program Generation

                    int rangeSize = _maxNonces / _threads;

                    if(rangeSize == 0)
                    {
                        rangeSize++;
                    }

                    var rangePartitioner = Partitioner.Create(0, _maxNonces, rangeSize);

                    Parallel.ForEach(rangePartitioner, new ParallelOptions { MaxDegreeOfParallelism = _threads }, (range, loopState) =>
                    {
                        Span<Instruction> allInstructions = cpuData.InstructionData.AsSpan();
                        Span<SipState> keys = cpuData.Keys.AsSpan();

                        byte[] currentChallenge = new byte[40];
                        _info.Challenge.CopyTo(currentChallenge, 0);
                        var currentChallengeSpan = currentChallenge.AsSpan();

                        // Loop over each range element without a delegate invocation.
                        for (int z = range.Item1; z < range.Item2; z++)
                        {
                            if (z % 32 == 0)
                            {
                                if (_hasNotice)
                                {
                                    return;
                                }
                            }

                            ulong currentNonce = 0;
                            HashX program = null;
                            var instructions = allInstructions.Slice(Instruction.TotalInstructions * z, Instruction.TotalInstructions);

                            //Keep trying until a valid program is found
                            while (true)
                            {
                                currentNonce = Interlocked.Increment(ref _currentNonce) - 1;
                                BinaryPrimitives.WriteUInt64LittleEndian(currentChallengeSpan.Slice(32), currentNonce);

                                //Invalid program
                                if (!HashX.TryBuild(currentChallenge, instructions, out program))
                                {
                                    continue;
                                }

                                break;
                            }

                            bool valid = true;

                            for (int i = 0; i < instructions.Length; i++)
                            {
                                var instruction = instructions[i];

                                instruction.SetDestination((ulong)instruction.Dst);
                                if (instruction.Type == OpCode.XorConst)
                                {
                                    instruction.SetType(OpCode.Xor);
                                    instruction.Src = (int)instruction.Operand;

                                    if (instruction.Src < 8 && instruction.Src >= 0)
                                    {
                                        valid = false;
                                        break;
                                    }
                                }
                                else if (instruction.Type == OpCode.AddConst)
                                {
                                    instruction.SetType(OpCode.Sub);
                                    instruction.Src = ((int)instruction.Operand * -1);

                                    if (instruction.Src < 8 && instruction.Src >= 0)
                                    {
                                        valid = false;
                                        break;
                                    }
                                }
                                else if (instruction.Type == OpCode.Branch)
                                {
                                    instruction.Dst = instruction.Operand;
                                }

                                if (instruction.Type == OpCode.Sub)
                                {
                                    instruction.SetType(OpCode.AddShift);
                                    instruction.SetOperand(-1);
                                }
                                else if (instruction.Type == OpCode.AddShift)
                                {
                                    var nOperand = 1 << instruction.Operand;

                                    instruction.SetOperand(nOperand);
                                }

                                instructions[i] = instruction;
                            }

                            if (valid)
                            {
                                keys[z] = new SipState
                                {
                                    V0 = program.RegisterKey.V0,
                                    V1 = program.RegisterKey.V1,
                                    V2 = program.RegisterKey.V2,
                                    V3 = program.RegisterKey.V3,
                                };

                                cpuData.NoncesUsed[z] = currentNonce;
                            }
                        }
                    });

                    #endregion

                    if (_hasNotice)
                    {
                        if (cpuData != null)
                        {
                            _availableCPUData.TryAdd(cpuData);
                        }

                        continue;
                    }

                    _setupCPUData.TryAdd(cpuData);

                    if (_currentNonce >= _info.EndNonce)
                    {
                        _logger.Log(LogLevel.Warn, $"Ran through all nonces set for the GPU. Total: {_info.EndNonce - _info.StartNonce} nonces");

                        PauseMining();
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, ex, $"Unknown exception occurred during GPU program generation. Message: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _newChallengeWait.Reset();

            if (_pool != null)
            {
                _pool.OnChallengeUpdate -= _pool_OnChallengeUpdate;
            }

            List<Task> waitTasks = new List<Task>();
            
            //Wait for all devices to stop
            _gpuDevices.ForEach(x => waitTasks.Add(x.WaitForStopAsync()));
            await Task.WhenAll(waitTasks);

            //Clean up CPU Data
            while(_availableCPUData.TryTake(out var item) || _setupCPUData.TryTake(out item))
            {
                item.Dispose();
            }

            //Clean up memory
            _gpuDevices.ForEach(x =>
            {
                x.OnDifficultyUpdate -= DHasher_OnDifficultyUpdate;
                x.OnHashrateUpdate -= DHasher_OnHashrateUpdate;
                x.Dispose();
            });
            _gpuDevices.Clear();

            //Clean up GPUData
            _gpuData.ForEach(x => x.Dispose());
            _gpuData.Clear();

            _context?.Dispose();
        }

        private void _pool_OnChallengeUpdate(object? sender, NewChallengeInfo e)
        {
            //Don't want to block pool module thread waiting for challenge to change
            Task.Run(() => NewChallenge(e.ChallengeId, e.Challenge, e.GPUStartNonce, e.GPUEndNonce));
        }

        #region GPU Implemention

        public abstract Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> HashxKernel();
        public abstract KernelConfig GetHashXKernelConfig(Device device, int maxNonces);
        public abstract Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> EquihashKernel();
        public abstract KernelConfig GetEquihashKernelConfig(Device device, int maxNonces);
        public bool IsSupported()
        {
            List<Device> validDevices = GetDevices(true);

            return validDevices?.Count > 0;
        }

        public List<Device> GetDevices(bool onlyValid)
        {
            try
            {
                using Context context = Context.Create((builder) => builder.AllAccelerators());

                if (onlyValid)
                {
                    return GetValidDevices(context.Devices);
                }
                else
                {
                    return context.Devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();
                }
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        protected virtual List<Device> GetValidDevices(IEnumerable<Device> devices)
        {
            if(devices == null)
            {
                return new List<Device>();
            }

            return devices.Where(x => x.AcceleratorType != AcceleratorType.CPU).ToList();
        }

        #endregion

        #region Generic Implementation

        public bool NewChallenge(int challengeId, Span<byte> challenge, ulong startNonce, ulong endNonce)
        {
            _newChallengeWait.Reset();

            //Sequence is same as previous
            if (challenge.SequenceEqual(_info.Challenge))
            {
                return true;
            }

            //Stop all GPU threads
            _gpuDevices.ForEach(x => x.PauseMining());

            //Wait for everything to stop
            while(_gpuDevices.Any(x => x.Executing) || _executing)
            {
                Thread.Sleep(50);
            }

            //Move all data over
            while(_setupCPUData.TryTake(out var item))
            {
                _availableCPUData.TryAdd(item);
            }

            _info.NewChallenge(startNonce, endNonce, challenge.ToArray(), challengeId);
            _currentNonce = startNonce;
            _newChallengeWait.Set();
            _pauseMining.Set();
            _gpuDevices.ForEach(x =>
            {
                x.ResetData();
                x.ResumeMining();
            });
            _challengeStartTime = _sw.Elapsed;
            _logger.Log(LogLevel.Debug, $"[GPU] New challenge. Challenge Id: {challengeId}. Range: {startNonce} - {endNonce}");

            return true;
        }

        public void SetThreads(int totalThreads)
        {
            _threads = totalThreads;
        }

        public void PauseMining()
        {
            _pauseMining.Reset();
        }

        public void ResumeMining()
        {
            _pauseMining.Set();
        }

        #endregion

        #region GPU Hasher

        private class GPUDeviceHasher : IDisposable
        {
            #region Consts

            public const ulong ProgramSize = (Instruction.TotalInstructions * Instruction.ByteSize);
            public const ulong KeySize = SipState.Size;
            public const ulong HeapSize = 2239488;
            public const ulong SolutionSize = EquixSolution.Size * EquixSolution.MaxLength;
            public const ulong HashSolutionSize = (ushort.MaxValue + 1) * sizeof(ulong);
            public const ulong MemoryPerNonce = (ProgramSize + KeySize + SolutionSize + HashSolutionSize) * _maxQueueSize + HeapSize;

            #endregion

            public event EventHandler OnDifficultyUpdate;
            public event EventHandler<HashrateInfo> OnHashrateUpdate;

            public bool Executing => !_copyToWaiting || !_copyFromWaiting || !_executingWaiting;
            private bool _copyToWaiting;
            private bool _copyFromWaiting;
            private bool _executingWaiting;

            private List<GPUDeviceData> _deviceData = new List<GPUDeviceData>();

            //Used to verify GPU solutions
            private Solver _solver = new Solver();

            private int _deviceId = 0;
            private Accelerator _accelerator = null;
            private Device _device = null;
            private KernelConfig _hashxConfig;
            private KernelConfig _equihashConfig;
            private HasherInfo _hasherInfo;


            private BlockingCollection<CPUData> _readyCPUData;
            private BlockingCollection<CPUData> _availableCPUData;
            private Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> _hashxKernel;
            private Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> _equihashKernel;

            private int _nonceCount = 0;

            private Task _runningTask = null;
            private CancellationTokenSource _runToken;
            private bool _running => !_runToken.IsCancellationRequested;

            private Task _gpuCopyToTask = null;
            private Task _gpuCopyFromTask = null;


            private ManualResetEvent _pauseMining = new ManualResetEvent(true);
            private bool _miningPaused => !_pauseMining.WaitOne(0);

            private bool _hasNotice => _miningPaused || !_running;

            public GPUDeviceHasher(Action<ArrayView<Instruction>, ArrayView<SipState>, ArrayView<ulong>> hashxKernel,
                         Action<ArrayView<ulong>, ArrayView<EquixSolution>, ArrayView<ushort>, ArrayView<uint>> equihashKernel,
                         Accelerator accelerator, Device device, int deviceId,
                         BlockingCollection<CPUData> readyCPUData, 
                         BlockingCollection<CPUData> availableCPUData,
                         HasherInfo hasherInfo,
                         KernelConfig hashxConfig,
                         KernelConfig equihashConfig)
            {
                _hashxKernel = hashxKernel;
                _equihashKernel = equihashKernel;
                _readyCPUData = readyCPUData;
                _availableCPUData = availableCPUData;
                _deviceId = deviceId;
                _device = device;
                _accelerator = accelerator;
                _hashxConfig = hashxConfig;
                _equihashConfig = equihashConfig;
                _hasherInfo = hasherInfo;

                if(accelerator is CudaAccelerator cudaAccelerator)
                {
                    //Equihash is better with equal or L1 preference
                    //Baseline suffers with L1 preference
                    cudaAccelerator.CacheConfiguration = CudaCacheConfiguration.PreferEqual;
                }
            }

            public async void Initialize(int totalNonces)
            {
                _nonceCount = totalNonces;

                MemoryBuffer1D<ushort, Stride1D.Dense> heap = _accelerator.Allocate1D<ushort>((uint)HeapSize * _nonceCount / 2);

                for (int i = 0; i < _maxQueueSize; i++)
                {
                    MemoryBuffer1D<Instruction, Stride1D.Dense> instructions = _accelerator.Allocate1D<Instruction>(_nonceCount * Instruction.TotalInstructions);
                    MemoryBuffer1D<SipState, Stride1D.Dense> keys = _accelerator.Allocate1D<SipState>(_nonceCount);
                    MemoryBuffer1D<ulong, Stride1D.Dense> hashes = _accelerator.Allocate1D<ulong>((ushort.MaxValue + 1) * _nonceCount);
                    MemoryBuffer1D<EquixSolution, Stride1D.Dense> solutions = _accelerator.Allocate1D<EquixSolution>(_nonceCount * EquixSolution.MaxLength);
                    MemoryBuffer1D<uint, Stride1D.Dense> solutionCounts = _accelerator.Allocate1D<uint>(_nonceCount);

                    GPUDeviceData deviceData = new GPUDeviceData(instructions, keys, heap, hashes, solutions, solutionCounts);
                    _deviceData.Add(deviceData);
                }

                _gpuCopyToTask = new Task(GPUCopyTo, TaskCreationOptions.LongRunning);
                _gpuCopyToTask.Start();

                _gpuCopyFromTask = new Task(GPUCopyFrom, TaskCreationOptions.LongRunning);
                _gpuCopyFromTask.Start();

                _runToken = new CancellationTokenSource();
                _runningTask = new Task(Execute, _runToken.Token, TaskCreationOptions.LongRunning);
                _runningTask.Start();

                _logger.Log(LogLevel.Debug, $"Initialized GPU device: {_device.Name}");
            }

            public async Task WaitForStopAsync()
            {
                List<Task> remainingTasks = new List<Task> { _runningTask, _gpuCopyFromTask, _gpuCopyToTask };

                _runToken.Cancel();
                await Task.WhenAll(remainingTasks);
                _runToken.Dispose();

                try
                {
                    foreach (var deviceData in _deviceData)
                    {
                        deviceData.Dispose();
                    }

                    _deviceData.Clear();
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to dispose GPU data");
                }
            }

            private void GPUCopyTo()
            {
                using var stream = _accelerator.CreateStream();

                try
                {
                    while (_running)
                    {
                        _copyToWaiting = true;

                        while ((!_pauseMining.WaitOne(500)) && _running)
                        {
                        }

                        _copyToWaiting = false;

                        GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.None);

                        while (deviceData == null && !_hasNotice)
                        {
                            Thread.Sleep(100);
                            deviceData = GetDeviceData(GPUDeviceData.Stage.None);
                        }

                        CPUData cpuData = null;

                        while (!_readyCPUData.TryTake(out cpuData, 50) && !_hasNotice)
                        {

                        }

                        if (_hasNotice)
                        {
                            //Return data
                            if (cpuData != null)
                            {
                                _logger.Log(LogLevel.Warn, $"Data returned");

                                _readyCPUData.TryAdd(cpuData);
                            }

                            //Mining paused, exiting, or new challenge are all handled by continuing loop
                            continue;
                        }

                        //Set as the current CPU data
                        deviceData.CurrentCPUData = cpuData;

                        //Copies to device.
                        deviceData.ProgramInstructions.View.CopyFromCPU(stream, cpuData.InstructionData);
                        deviceData.Keys.View.CopyFromCPU(stream, cpuData.Keys);

                        //Wait for copy to finish
                        stream.Synchronize();

                        deviceData.CurrentStage = GPUDeviceData.Stage.Execute;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during CPU->GPU copying. Reason: {ex.Message}");
                }
            }

            private void Execute()
            {
                _logger.Log(LogLevel.Debug, $"[GPU] Loading kernels to {_device.Name} [{_deviceId}]");

                var hashxKernel = _accelerator.LoadStreamKernel(_hashxKernel);
                var equixKernel = _accelerator.LoadStreamKernel(_equihashKernel);

                _logger.Log(LogLevel.Debug, $"[GPU] Finished loading kernels for {_device.Name} [{_deviceId}]");

                try
                {
                    while (_running)
                    {
                        _executingWaiting = true;

                        while ((!_pauseMining.WaitOne(500)) && _running)
                        {
                        }

                        _executingWaiting = false;

                        GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.Execute);

                        while (deviceData == null && !_hasNotice)
                        {
                            Thread.Sleep(100);
                            deviceData = GetDeviceData(GPUDeviceData.Stage.Execute);
                        }

                        if (_hasNotice)
                        {
                            continue;
                        }

                        using var marker1 = _accelerator.AddProfilingMarker();
                        hashxKernel(_hashxConfig, deviceData.ProgramInstructions.View, deviceData.Keys.View, deviceData.Hashes.View);
                        using var marker2 = _accelerator.AddProfilingMarker();
                        equixKernel(_equihashConfig, deviceData.Hashes.View, deviceData.Solutions.View, deviceData.Heap.View, deviceData.SolutionCounts.View);
                        using var marker3 = _accelerator.AddProfilingMarker();

                        //Wait for kernels to finish
                        _accelerator.Synchronize();

                        deviceData.CurrentStage = GPUDeviceData.Stage.Solution;
                        deviceData.HashXTime = marker2.MeasureFrom(marker1);
                        deviceData.EquihashTime = marker3.MeasureFrom(marker2);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during GPU execution. Reason: {ex.Message}");
                }
            }

            private void GPUCopyFrom()
            {
                using var stream = _accelerator.CreateStream();

                try
                {
                    while (_running)
                    {
                        _copyFromWaiting = true;

                        while ((!_pauseMining.WaitOne(500)) && _running)
                        {
                        }

                        _copyFromWaiting = false;

                        GPUDeviceData deviceData = GetDeviceData(GPUDeviceData.Stage.Solution);

                        while (deviceData == null && !_hasNotice)
                        {
                            Thread.Sleep(100);
                            deviceData = GetDeviceData(GPUDeviceData.Stage.Solution);
                        }

                        if (_hasNotice)
                        {
                            continue;
                        }

                        //Copies back to host
                        deviceData.Solutions.CopyToCPU(stream, deviceData.CurrentCPUData.Solutions);
                        deviceData.SolutionCounts.CopyToCPU(stream, deviceData.CurrentCPUData.SolutionCounts);

                        //Wait for copy to finish
                        stream.Synchronize();

                        ulong startSolutions = _hasherInfo.TotalSolutions;

                        #region Verify

                        Span<EquixSolution> allSolutions = deviceData.CurrentCPUData.Solutions;
                        Span<uint> solutionCounts = deviceData.CurrentCPUData.SolutionCounts;
                        byte[] challenge = new byte[40];
                        _hasherInfo.Challenge.CopyTo(challenge, 0);
                        byte[] b_nonceOutput = new byte[24];

                        Span<byte> hashOutput = new Span<byte>(new byte[32]);
                        Span<byte> nonceOutput = new Span<byte>(b_nonceOutput);
                        Span<ushort> testSolution = new Span<ushort>(new ushort[8]);

                        int totalSolutions = 0;
                        int currentBestDifficulty = _hasherInfo.DifficultyInfo.BestDifficulty;
                        int prevBestDifficulty = currentBestDifficulty;

                        for (int i = 0; i < deviceData.CurrentCPUData.NoncesUsed.Length; i++)
                        {
                            Span<EquixSolution> solutions = allSolutions.Slice(8 * i, 8);
                            uint solutionCount = Math.Min(8, solutionCounts[i]);

                            for (int z = 0; z < solutionCount; z++)
                            {
                                ++totalSolutions;

                                Span<ushort> eSolution = MemoryMarshal.Cast<EquixSolution, ushort>(solutions.Slice(z, 1));
                                eSolution.CopyTo(testSolution);

                                testSolution.Sort();

                                ulong nonce = deviceData.CurrentCPUData.NoncesUsed[i];

                                BinaryPrimitives.WriteUInt64LittleEndian(nonceOutput.Slice(16), nonce);
                                MemoryMarshal.Cast<ushort, byte>(testSolution).CopyTo(nonceOutput);
                                SHA3.Sha3Hash(testSolution, nonce, hashOutput);
                                int difficulty = CalculateTarget(hashOutput);

                                if (difficulty > currentBestDifficulty)
                                {
                                    //Verify
                                    //A small percent of solutions do end up invalid
                                    if (!VerifyResultAndReorder(nonce, solutions.Slice(z, 1)))
                                    {
                                        _logger.Log(LogLevel.Warn, $"Solution verification failed. Nonce: {nonce}. Solution: {solutions.Slice(z, 1)[0]}. Expected Difficulty: {difficulty}. Skipping");

                                        continue;
                                    }

                                    currentBestDifficulty = difficulty;
                                    _hasherInfo.UpdateDifficulty(difficulty, MemoryMarshal.Cast<ushort, byte>(eSolution).ToArray(), nonce);
                                }

                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                bool VerifyResultAndReorder(ulong nonce, Span<EquixSolution> solution)
                                {
                                    BinaryPrimitives.WriteUInt64LittleEndian(challenge.AsSpan().Slice(32), nonce);
                                    HashX.TryBuild(challenge, out var program);
                                    program.InitCompiler(_solver.CompiledProgram);

                                    var result = _solver.Verify(program, solution[0]);

                                    var asdfasdf = program.Emulate(0);

                                    program.DestroyCompiler();

                                    if (result != Solver.EquixResult.EquixOk)
                                    {
                                        return false;
                                    }

                                    //Reorder

                                    //Individual level
                                    Span<ushort> v = MemoryMarshal.Cast<EquixSolution, ushort>(solution);

                                    for (int i = 0; i < v.Length; i += 2)
                                    {
                                        if (v[i] > v[i + 1])
                                        {
                                            var t = v[i];
                                            v[i] = v[i + 1];
                                            v[i + 1] = t;
                                        }
                                    }

                                    //First pair
                                    Span<uint> v2 = MemoryMarshal.Cast<ushort, uint>(v);

                                    for (int i = 0; i < v2.Length; i += 2)
                                    {
                                        if (v2[i] > v2[i + 1])
                                        {
                                            var t = v2[i];
                                            v2[i] = v2[i + 1];
                                            v2[i + 1] = t;
                                        }
                                    }


                                    //Second pair
                                    Span<ulong> v4 = MemoryMarshal.Cast<uint, ulong>(v2);

                                    for (int i = 0; i < v4.Length; i += 2)
                                    {
                                        if (v4[i] > v4[i + 1])
                                        {
                                            var t = v4[i];
                                            v4[i] = v4[i + 1];
                                            v4[i + 1] = t;
                                        }
                                    }

                                    return true;
                                }
                            }

                            //Only notifies that there was a better difficulty found
                            if (currentBestDifficulty > prevBestDifficulty)
                            {
                                OnDifficultyUpdate?.Invoke(this, EventArgs.Empty);
                            }
                        }

                        #endregion

                        deviceData.CurrentStage = GPUDeviceData.Stage.None;
                        _hasherInfo.AddSolutionCount((ulong)totalSolutions);

                        OnHashrateUpdate?.Invoke(this, new HashrateInfo
                        {
                            Index = _deviceId,
                            ExecutionTime = deviceData.ExecutionTime,
                            GPUEquihashTime = deviceData.EquihashTime,
                            GPUHashXTime = deviceData.HashXTime,
                            NumNonces = (ulong)_nonceCount,
                            NumSolutions = _hasherInfo.TotalSolutions - startSolutions,
                            HighestDifficulty = _hasherInfo.DifficultyInfo.BestDifficulty,
                            ChallengeSolutions = _hasherInfo.TotalSolutions,
                            CurrentThreads = -1,
                            ChallengeId = _hasherInfo.ChallengeId
                        });

                        //Return data
                        _availableCPUData.TryAdd(deviceData.CurrentCPUData);

                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Unknown exception occurred during GPU->CPU copying. Reason: {ex.Message}");
                }
    }

            public void PauseMining()
            {
                _pauseMining.Reset();
            }

            public void ResumeMining()
            {
                _pauseMining.Set();
            }

            public void ResetData()
            {
                foreach(var deviceData in _deviceData)
                {
                    deviceData.CurrentStage = GPUDeviceData.Stage.None;
                    deviceData.CurrentCPUData = null;
                }
            }

            private GPUDeviceData GetDeviceData(GPUDeviceData.Stage stage)
            {
                return _deviceData.FirstOrDefault(x => x.CurrentStage == stage);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int CalculateTarget(ReadOnlySpan<byte> s)
            {
                int totalBits = 0;

                for (int i = 0; i < s.Length; i++)
                {
                    var t = BitOperations.LeadingZeroCount(s[i]) - 24;
                    totalBits += t;

                    if (t < 8)
                    {
                        break;
                    }
                }

                return totalBits;
            }

            public void Dispose()
            {
                try
                {
                    _solver.Dispose();
                    _accelerator?.Dispose();
                }
                catch(Exception ex)
                {
                    _logger.Log(LogLevel.Error, ex, $"Failed to clean up memory for GPU device");
                }
            }

            private class GPUDeviceData : IDisposable
            {
                public enum Stage { None, Execute, Solution };

                public Stage CurrentStage { get; set; } = Stage.None;
                public TimeSpan HashXTime { get; set; }
                public TimeSpan EquihashTime { get; set; }
                public TimeSpan ExecutionTime => HashXTime + EquihashTime;

                //Unique
                public MemoryBuffer1D<Instruction, Stride1D.Dense> ProgramInstructions { get; private set; }
                public MemoryBuffer1D<SipState, Stride1D.Dense> Keys { get; private set; }
                public MemoryBuffer1D<ulong, Stride1D.Dense> Hashes { get; private set; }
                public MemoryBuffer1D<EquixSolution, Stride1D.Dense> Solutions { get; private set; }
                public MemoryBuffer1D<uint, Stride1D.Dense> SolutionCounts { get; private set; }

                //These only need a single copy
                public MemoryBuffer1D<ushort, Stride1D.Dense> Heap { get; private set; }

                public CPUData CurrentCPUData { get; set; }


                public GPUDeviceData(MemoryBuffer1D<Instruction, Stride1D.Dense> programInstructions, 
                                     MemoryBuffer1D<SipState, Stride1D.Dense> keys,
                                     MemoryBuffer1D<ushort, Stride1D.Dense> heap,
                                     MemoryBuffer1D<ulong, Stride1D.Dense> hashes,
                                     MemoryBuffer1D<EquixSolution, Stride1D.Dense> solutions,
                                     MemoryBuffer1D<uint, Stride1D.Dense> solutionsCounts)
                {
                    ProgramInstructions = programInstructions;
                    Keys = keys;
                    Heap = heap;
                    Hashes = hashes;
                    Solutions = solutions;
                    SolutionCounts = solutionsCounts;
                }

                public void Dispose()
                {
                    ProgramInstructions?.Dispose();
                    Keys?.Dispose();
                    Hashes?.Dispose();
                    Solutions?.Dispose();
                    SolutionCounts?.Dispose();

                    //This isn't unique to this object, so check if they weren't disposed of already
                    if (Heap?.IsDisposed != true)
                    {
                        Heap?.Dispose();
                    }
                }
            }
        }

        #endregion

        #region CPU Data

        private unsafe class CPUData : IDisposable
        {
            //If copies are too slow, can change this back to pinned

            public Instruction[] InstructionData { get; private set; }
            public SipState[] Keys { get; private set; }
            public EquixSolution[] Solutions { get; private set; }
            public uint[] SolutionCounts { get; private set; }
            public ulong[] NoncesUsed { get; private set; }

            //private Instruction* _instructionData;
            //private SipState* _keys;
            //private EquixSolution* _solutions;

            public CPUData(int nonceCount)
            {
                InstructionData = new Instruction[nonceCount * Instruction.TotalInstructions];
                Keys = new SipState[nonceCount];
                Solutions = new EquixSolution[EquixSolution.MaxLength * nonceCount];
                SolutionCounts = new uint[nonceCount];
                NoncesUsed = new ulong[nonceCount];
            }

            public void Dispose()
            {
                //NativeMemory.Free(_instructionData);
                //NativeMemory.Free(_keys);
                //NativeMemory.Free(_solutions);
            }
        }

        #endregion
    }
}