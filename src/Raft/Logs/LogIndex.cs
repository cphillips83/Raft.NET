﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Raft.Logs
{
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 0)]
    public struct LogIndex
    {
        [FieldOffset(0)]
        public int Term;

        [FieldOffset(4)]
        public LogIndexType Type;

        [FieldOffset(8)]
        public uint Offset;

        [FieldOffset(12)]
        public uint Size;
    }

}