﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Raft.Logs;
using Raft.Messages;
using Raft.States;
using Raft.Transports;

namespace Raft
{
    public class Coroutine
    {
        private IEnumerator<object> _executor;
        public Coroutine(IEnumerable<object> coroutine)
        {
            _executor = coroutine.GetEnumerator();
        }

        public bool IsDone { get; private set; }

        public void Signal()
        {
            if (!IsDone)
            {
                if (!_executor.MoveNext())
                    IsDone = true;
                else// if(!string.IsNullOrEmpty(_executor.Current))
                {
                    //check object type and do something?
                    //Failed = true;
                    //IsDone = true;
                }
            }
        }
    }

    public class Server : IDisposable
    {
        private static object _syncLock = new object();

        //private NetPeer _rpc;
        private volatile bool _clearCoroutines = false;
        private Coroutine _currentSyncRoutine;
        private ConcurrentQueue<Coroutine> _syncQueue = new ConcurrentQueue<Coroutine>();
        private List<Coroutine> _asyncCommand = new List<Coroutine>();

        private bool _inited = false;
        private IPEndPoint _id;
        private uint _commitIndex = 0;
        private Random _random;
        //private Stopwatch _timer;
        private Log _persistedStore;
        private ITransport _transport;
        private List<Client> _clients = new List<Client>();

        private AbstractState _currentState;
        private long _tick = 0;

        public string Name { get { return "LV-" + Volume.ToString("D4"); } }

        public IPEndPoint ID { get { return _id; } }

        public uint CommitIndex { get { return _commitIndex; } set { _commitIndex = value; } }

        public IPEndPoint AgentIP { get; set; }

        public long Tick { get { return _tick; } }
        //public long RawTimeInMS { get { return _timer.ElapsedMilliseconds; } }

        public ITransport Transport { get { return _transport; } }
        //public NetPeer IO { get { return _rpc; } }

        public Log PersistedStore { get { return _persistedStore; } }

        public Random Random { get { return _random; } }

        public AbstractState CurrentState { get { return _currentState; } }

        public int Majority { get { return ((_clients.Count() + 1) / 2) + 1; } }

        public long FreeSpaceInBytes { get { return _persistedStore.FreeSpaceInBytes; } }

        public float FreeSpace { get { return _persistedStore.FreeSpace; } }

        public long SpaceUsedInBytes { get { return _persistedStore.DataPosition; } }

        public int Volume { get; protected set; }

        public IEnumerable<Client> Voters
        {
            get
            {
                foreach (var client in _clients)
                    yield return client;
            }
        }

        public IEnumerable<Client> Clients
        {
            get
            {
                foreach (var client in _clients)
                    yield return client;
            }
        }

        public Server(int volume, IPEndPoint id, Log log, ITransport transport)
        {
            //_config = config;
            Volume = volume;
            _id = id;
            _random = new Random((int)DateTime.UtcNow.Ticks ^ _id.GetHashCode());
            //_dataDir = dataDir;
            _currentState = new StoppedState(this);
            _transport = transport;
            _persistedStore = log;
        }

        public Client GetClient(IPEndPoint id)
        {
            var client = _clients.FirstOrDefault(x => x.ID.Equals(id));
            if (client == null)
                client = new Client(this, id);

            return client;
        }

        public void AddClientFromLog(IPEndPoint id)
        {
            System.Diagnostics.Debug.Assert(_clients.Count(x => x.ID.Equals(id)) == 0);

            var client = new Client(this, id);
            client.ResetKnownLogs();
            _clients.Add(client);
        }

        public void RemoveClientFromLog(IPEndPoint id)
        {
            System.Diagnostics.Debug.Assert(GetClient(id) != null);

            for (var i = 0; i < _clients.Count; i++)
            {
                if (_clients[i].ID.Equals(id))
                {
                    _clients.RemoveAt(i);
                    return;
                }
            }

            System.Diagnostics.Debug.Assert(false);
        }

        public void Initialize(params IPEndPoint[] clients)
        {
            if (!_inited)
            {
                _inited = true;
                //_persistedStore = log;
                //_persistedStore.Initialize();

                if (clients != null && clients.Length > 0)
                    _persistedStore.UpdateClients(clients);
                //else if (!bootstrap)
                //    _persistedStore.AddServer(this, _id);

                foreach (var client in _persistedStore.Clients)
                {
                    if (!client.Equals(_id))
                        _clients.Add(new Client(this, client));
                }


                //_timer = Stopwatch.StartNew();

                //ChangeState(new FollowerState(this));

                //var binding = new BasicHttpBinding();
                //var custom = new CustomBinding(binding);
                //custom.Elements.Find<HttpTransportBindingElement>().KeepAliveEnabled = false;

                //var host = new ServiceHost(this, endpoint);

                //host.AddServiceEndpoint(typeof(INodeProxy), custom, endpoint);

                //host.Description.Behaviors.Add(new ServiceMetadataBehavior()
                //{
                //    HttpGetEnabled = true,
                //    MetadataExporter = { PolicyVersion = PolicyVersion.Policy15 },
                //});

                //host.Open();
                //transport.Start(_id);

                _commitIndex = _persistedStore.LastAppliedIndex;
                Console.Write(_persistedStore.ToString(_id));
            }

        }

        public void ChangeState(AbstractState newState)
        {
            if (_currentState != null)
                _currentState.Exit();

            _currentState = newState;
            _currentState.Enter();
        }

        public void AdvanceCommits()
        {
            var _persistedStore = PersistedStore;

            var matchIndexes = new uint[_clients.Count + 1];
            matchIndexes[matchIndexes.Length - 1] = _persistedStore.Length;
            for (var i = 0; i < _clients.Count; i++)
                matchIndexes[i] = _clients[i].MatchIndex;

            Array.Sort(matchIndexes);

            var n = matchIndexes[_clients.Count / 2];
            if (_persistedStore.GetTerm(n) == _persistedStore.Term)
                AdvanceToCommit(Math.Max(CommitIndex, n));
        }

        private void ProcessAsyncCoroutines()
        {
            if (_clearCoroutines)
            {
                lock (_asyncCommand)
                {
                    _asyncCommand.Clear();
                }
                return;
            }

            Coroutine[] routines = null;
            lock (_asyncCommand)
            {
                if (_asyncCommand.Count > 0)
                    routines = _asyncCommand.ToArray();
            }

            if (routines != null)
            {
                var anyFinished = false;
                for (var i = 0; i < routines.Length; i++)
                {
                    routines[i].Signal();

                    if (routines[i].IsDone)
                        anyFinished = true;
                }

                if (anyFinished)
                {
                    lock (_asyncCommand)
                    {
                        for (int i = routines.Length - 1; i >= 0; i--)
                        {
                            if (routines[i].IsDone)
                                _asyncCommand.RemoveAt(i);
                        }
                    }
                }
            }
        }

        public void ProcessSyncCoroutines()
        {
            if (_clearCoroutines)
            {
                _currentSyncRoutine = null;
                while (!_syncQueue.IsEmpty)
                {
                    Coroutine coroutine;
                    _syncQueue.TryDequeue(out coroutine);
                }
                return;
            }

            if (_currentSyncRoutine != null)
            {
                _currentSyncRoutine.Signal();

                if (_currentSyncRoutine.IsDone)
                    _currentSyncRoutine = null;
            }

            if (_syncQueue.IsEmpty)
                return;

            if (_syncQueue.TryDequeue(out _currentSyncRoutine))
                ProcessSyncCoroutines();
        }

        public void Process()
        {
            _transport.Process(this);

            foreach (var client in Clients)
                client.Update();

            _currentState.Update();

            ProcessAsyncCoroutines();
            ProcessSyncCoroutines();
            _clearCoroutines = false;
        }

        public void Advance()
        {
            _tick++;
            Process();
        }

        public void Advance(long ticks)
        {
            while (ticks-- > 0)
                Advance();
        }

        public void AdvanceTo(long tick)
        {
            while (_tick < tick)
                Advance();
        }

        public void Dispose()
        {
            if (_transport != null)
                _transport.Shutdown();

            if (_persistedStore != null)
                _persistedStore.Dispose();

            _clients.Clear();

            _transport = null;
            _persistedStore = null;
            _currentState = null;
        }

        //static readonly NLog.LogManager.GetLogger("Server", typeof(Server));
        public bool AdvanceToCommit(uint newCommitIndex)
        {
            // cap newCommitIndex to our logs length
            newCommitIndex = Math.Min(newCommitIndex, _persistedStore.Length);

            if (newCommitIndex != _commitIndex)
            {
                // make sure newCommitIndex isn't behind our already applied index
                newCommitIndex = Math.Max(newCommitIndex, _persistedStore.LastAppliedIndex);

                //Console.WriteLine("{0}: Advancing commit index from {1} to {2}", _id, _commitIndex, newCommitIndex);
                for (var i = _persistedStore.LastAppliedIndex; i < newCommitIndex; i++)
                {
                    _persistedStore.ApplyIndex(this, i + 1);
                }
                _commitIndex = newCommitIndex;
                return true;
            }

            return false;
        }

        public void QueueCoroutine(IEnumerable<object> coroutine, bool async)
        {
            if (async)
            {
                lock (_asyncCommand)
                    _asyncCommand.Add(new Coroutine(coroutine));
            }
            else
                _syncQueue.Enqueue(new Coroutine(coroutine));
        }

        public void ClearCoroutines()
        {
            _clearCoroutines = true;
        }
    }
}
