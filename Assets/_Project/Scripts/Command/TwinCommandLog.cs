using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.Command
{
    /// <summary>
    /// 命令审计日志（内存环形 500 条）。
    ///
    /// 【为什么 P12 就要做】演示时被问「你怎么知道这条命令真的执行了」，
    /// 答「有日志，能看到从下发到回读确认的全过程」比答「按钮变绿了」可信得多。
    /// 成本是一个队列 + 一行 Console。
    ///
    /// 【为什么是环形而不是无限增长】一次演示可能点几百次，长时间挂着不重启会吃内存。
    /// 500 条足够覆盖任何一次演示，超出就从最早的丢。
    /// </summary>
    public sealed class TwinCommandLog
    {
        public struct Entry
        {
            public double AtUtc;
            public string Time;
            public string Operator;
            public string TwinId;
            public string Action;
            public int Priority;
            public string Result;
            public string Reason;
            public double Elapsed;
            public bool Ok;
        }

        public const int Capacity = 500;

        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly Queue<Entry> _entries = new Queue<Entry>(Capacity + 8);
        private readonly List<Entry> _recentBuf = new List<Entry>(32);

        /// <summary>最近一条的整行文本（面板可以拿它做「上一条命令」提示）。</summary>
        public string LastLine { get; private set; }

        public int Count { get { return _entries.Count; } }

        public TwinCommandLog()
        {
            LastLine = "";
        }

        public void Add(TwinCommand cmd, string principal)
        {
            if (cmd == null) return;

            Entry e = new Entry();
            e.AtUtc = cmd.CompletedAtUtc > 0d ? cmd.CompletedAtUtc : TwinClock.NowUtcSeconds();
            e.Time = FormatTime(e.AtUtc);
            e.Operator = string.IsNullOrEmpty(principal) ? "local-user" : principal;
            e.TwinId = cmd.TwinId;
            e.Action = cmd.ActionText;
            e.Priority = cmd.Priority;
            e.Ok = cmd.State == CommandState.Confirmed;
            e.Result = e.Ok ? "Confirmed" : "Failed";
            e.Reason = cmd.Reason;
            e.Elapsed = cmd.ElapsedSeconds;

            _entries.Enqueue(e);
            while (_entries.Count > Capacity) _entries.Dequeue();

            LastLine = ToLine(e);
            Debug.Log("[TwinCmd] " + LastLine);
        }

        /// <summary>最近 n 条（新的在前）。返回的列表是内部缓冲，下次调用会被覆盖。</summary>
        public IReadOnlyList<Entry> Recent(int n)
        {
            _recentBuf.Clear();
            if (n <= 0 || _entries.Count == 0) return _recentBuf;

            Entry[] all = _entries.ToArray();
            int start = Mathf.Max(0, all.Length - n);
            for (int i = all.Length - 1; i >= start; i--) _recentBuf.Add(all[i]);
            return _recentBuf;
        }

        public void Clear()
        {
            _entries.Clear();
            _recentBuf.Clear();
            LastLine = "";
        }

        private static string ToLine(Entry e)
        {
            string s = e.Time + "  " + e.Operator + "  " + e.TwinId + "  " + e.Action +
                       "  P" + e.Priority + "  " + e.Result;
            if (!e.Ok && !string.IsNullOrEmpty(e.Reason)) s += "(" + e.Reason + ")";
            s += "  " + e.Elapsed.ToString("0.00") + "s";
            return s;
        }

        private static string FormatTime(double utcSeconds)
        {
            try
            {
                DateTime t = Epoch.AddSeconds(utcSeconds).ToLocalTime();
                return t.ToString("HH:mm:ss.fff");
            }
            catch (Exception)
            {
                return "--:--:--.---";
            }
        }
    }
}
