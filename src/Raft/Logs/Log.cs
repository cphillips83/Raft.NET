﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Raft.Logs
{
    public abstract class Log : IDisposable
    {
        public int RPC_TIMEOUT = 50;
        public int ELECTION_TIMEOUT = 100;

        //can not change once in production
        public const int SUPER_BLOCK_SIZE = 1024;
        public const int LOG_DEFAULT_ARRAY_SIZE = 65536;
        public const int LOG_RECORD_SIZE = 16;
        //public const int MAX_LOG_DATA_READS = 16;

        // current term of the cluster
        private int _currentTerm;
        private uint _appliedIndex;

        // who we last voted for
        private int? _votedFor;

        private Stream _indexStream, _logDataFile;
        private BinaryWriter _logIndexWriter;

        // log index
        private LogIndex[] _logIndices;
        private uint _logLength;

        //private List<Peer> _peers;
        private List<Configuration> _clients = new List<Configuration>();

        public IEnumerable<Configuration> Clients { get { return _clients; } }

        public int Term
        {
            get { return _currentTerm; }
            set
            {
                _currentTerm = value;
                saveSuperBlock();
            }
        }

        public int? VotedFor
        {
            get { return _votedFor; }
            set
            {
                _votedFor = value;
                saveSuperBlock();
            }
        }

        //public string DataDirectory { get { return _dataDir; } }
        //public string IndexFile { get { return _indexFilePath; } }
        //public string DataFile { get { return _dataFilePath; } }
        public uint Length { get { return _logLength; } }

        public uint DataPosition
        {
            get
            {
                if (_logLength == 0)
                    return 0;

                var index = _logIndices[_logLength - 1];
                return index.Offset + index.Size;
            }
        }

        protected Log()
        {
            //_peers = new List<Peer>();
            //_server = server;
            //_nodeSettings = nodeSettings;

            //_dataDir = dataDir;
            //_indexFilePath = System.IO.Path.Combine(dataDir, "index");
            //_dataFilePath = System.IO.Path.Combine(dataDir, "data");
        }

        protected abstract Stream OpenIndexFile();

        protected abstract Stream OpenDataFile();

        private void readState(BinaryReader br)
        {
            System.Diagnostics.Debug.Assert(br.BaseStream.Length >= SUPER_BLOCK_SIZE);
            System.Diagnostics.Debug.Assert(((br.BaseStream.Length - SUPER_BLOCK_SIZE)) % LOG_RECORD_SIZE == 0);

            //read term and last vote
            _currentTerm = br.ReadInt32();
            _votedFor = br.ReadBoolean() ? (int?)br.ReadInt32() : null;
            _appliedIndex = br.ReadUInt32();

            // peers
            var peerCount = br.ReadInt32();
            for (var i = 0; i < peerCount; i++)
            {
                var id = br.ReadInt32();
                var addrBytesLen = br.ReadInt32();
                var addrBytes = br.ReadBytes(addrBytesLen);
                var port = br.ReadInt32();

                _clients.Add(new Configuration(id, new System.Net.IPAddress(addrBytes), port));
            }

            //seek to end of superblock for data
            br.BaseStream.Seek(SUPER_BLOCK_SIZE, SeekOrigin.Begin);

            //get record count
            var indices = (uint)((br.BaseStream.Length - SUPER_BLOCK_SIZE) / LOG_RECORD_SIZE);
            ensureLogIndices(indices);

            //read records in
            for (var i = 0; i < indices; i++)
            {
                _logIndices[i].Term = br.ReadInt32();
                _logIndices[i].Type = (LogIndexType)br.ReadUInt32();
                _logIndices[i].Offset = br.ReadUInt32();
                _logIndices[i].Size = br.ReadUInt32();
            }

            //update log index
            _logLength = indices;
        }

        private void createSuperBlock()
        {
            //write empty state so that base stream position is at the end of our data
            saveSuperBlock();

            //pad remaining data with 0s
            _logIndexWriter.Write(new byte[SUPER_BLOCK_SIZE - _logIndexWriter.BaseStream.Position]);
            _logIndexWriter.Flush();

            //init default log entry size
            _logIndices = new LogIndex[LOG_DEFAULT_ARRAY_SIZE];

        }

        private bool saveSuperBlock()
        {
            // move to start of super block
            _logIndexWriter.Seek(0, SeekOrigin.Begin);

            // write current term
            _logIndexWriter.Write(_currentTerm);

            // did we vote?
            _logIndexWriter.Write(_votedFor.HasValue);

            // who did we vote for
            _logIndexWriter.Write(_votedFor.HasValue ? _votedFor.Value : -1);

            // last applied index
            _logIndexWriter.Write(_appliedIndex);

            // peers
            _logIndexWriter.Write(_clients.Count);
            for (var i = 0; i < _clients.Count; i++)
            {
                _logIndexWriter.Write(_clients[i].ID);
                
                var addrBytes = _clients[i].IP.GetAddressBytes();
                _logIndexWriter.Write(addrBytes.Length);
                _logIndexWriter.Write(addrBytes);
                _logIndexWriter.Write(_clients[i].Port);
            }

            // ensure its on the HDD
            _logIndexWriter.Flush();

            return true;
        }

        private void ensureLogIndices(uint size)
        {
            // we don't want to increase the size yet
            // of a system is readonly it would create wasted memory
            if (_logIndices == null)
                _logIndices = new LogIndex[size];

            // do we need to increase?
            if (_logIndices.Length < size)
            {
                // calculate next size
                var newSize = Math.Max(_logIndices.Length * 3 / 2, LOG_DEFAULT_ARRAY_SIZE);

                // are we still too small?
                while (newSize < size)
                    newSize = newSize * 3 / 2;

                // resize array
                Array.Resize(ref _logIndices, newSize);
            }
        }

        public void Initialize()
        {
            _indexStream = OpenIndexFile();
            _logDataFile = OpenDataFile();

            if (_indexStream.Length > 0)
                using (var br = new BinaryReader(_indexStream))
                    readState(br);

            _logIndexWriter = new BinaryWriter(_indexStream);
            if (_indexStream.Length == 0)
                createSuperBlock();

        }

        public void UpdateState(int term, int? votedFor)
        {
            _currentTerm = term;
            _votedFor = votedFor;
            saveSuperBlock();
        }

        public LogEntry Create(byte[] data)
        {
            var entry = new LogEntry()
            {
                Index = new LogIndex()
                {
                    Term = _currentTerm,
                    Offset = DataPosition,
                    Size = (uint)data.Length,
                    Type = 0
                },
                Data = data
            };

            Push(entry);
            return entry;
        }

        public void Push(LogEntry data)
        {
            // we must first write the data to the dat file
            // in case of crash in between log data and log entry
            // this will orphan the data and on startup will reclaim the space

            // stream length is in UNSIGN but seek is SIGN?
            // seek before we commit the data so we are at the right position
            _logIndexWriter.Seek((int)(SUPER_BLOCK_SIZE + _logLength * LOG_RECORD_SIZE), SeekOrigin.Begin);

            // make sure we have enough capacity
            ensureLogIndices(_logLength + 1);

            //write to log data file
            _logDataFile.Seek(DataPosition, SeekOrigin.Begin);
            _logDataFile.Write(data.Data, 0, data.Data.Length);

            //update log entries
            _logIndices[_logLength] = data.Index;

            //inc log index
            _logLength++;
            //_logDataPosition += data.Index.Size;

            //flush data
            _logDataFile.Flush();

            //write data
            _logIndexWriter.Write(data.Index.Term);
            _logIndexWriter.Write((uint)data.Index.Type);
            _logIndexWriter.Write(data.Index.Offset);
            _logIndexWriter.Write(data.Index.Size);

            _logIndexWriter.Flush();
        }

        public void Pop()
        {
            System.Diagnostics.Debug.Assert(_logLength > 0);

            _logLength--;
        }

        public void UpdateClients(IEnumerable<Configuration> clients)
        {
            _clients.Clear();
            foreach (var client in clients)
                _clients.Add(client);

            saveSuperBlock();
        }

        public bool GetIndex(uint key, out LogIndex index)
        {
            if (key < 1 || key > _logLength)
            {
                index = new LogIndex() { Type = 0, Offset = 0, Size = 0, Term = 0 };
                return false;
            }

            index = _logIndices[key - 1];
            return true;
        }

        public int GetTerm(uint key)
        {
            if (key < 1 || key > _logLength)
                return 0;

            return _logIndices[key - 1].Term;
        }

        public int GetLastTerm()
        {
            return GetTerm(_logLength);
        }

        public uint GetLastIndex(out LogIndex index)
        {
            if (_logLength == 0)
            {
                index = new LogIndex() { Type = 0, Offset = 0, Size = 0, Term = 0 };
                return 0;
            }

            index = _logIndices[_logLength - 1];
            return _logLength;
        }

        public byte[] GetData(LogIndex index)
        {
            var data = new byte[index.Size];

            _logDataFile.Seek(index.Offset, SeekOrigin.Begin);
            _logDataFile.Read(data, 0, data.Length);
            return data;
        }

        public LogEntry? GetEntry(uint key)
        {
            if (key < 1 || key > _logLength)
                return null;

            var index = _logIndices[key - 1];
            var data = new byte[index.Size];

            _logDataFile.Seek(index.Offset, SeekOrigin.Begin);
            _logDataFile.Read(data, 0, data.Length);

            return new LogEntry() { Index = index, Data = data };
        }

        public LogEntry[] GetEntries(uint start, uint end)
        {
            if (start < 0 || end < 1 || start == end)
                return null;

            var entries = new LogEntry[end - start];
            for (var i = start; i < end; i++)
            {
                var entry = GetEntry(i + 1);
                System.Diagnostics.Debug.Assert(entry.HasValue);
                entries[i - start] = entry.Value;
            }

            return entries;
        }

        public void Dispose()
        {
            // Dispose could be called from a crash and could be called
            // at an unexpected time, not safe to save data here            
            //if (_logIndexWriter != null)
            //    _logIndexWriter.Dispose();


            // GC finalizer shouldn't call dipose(true)/close of these, so stale data
            // shouldn't be copied which is what we really want
            // then again since we write log data first and index data second it might not matter
            _logDataFile.Dispose();
            _logDataFile = null;

            _logIndexWriter.Dispose();
            _logIndexWriter = null;
        }
    }
}
