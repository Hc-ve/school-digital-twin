using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P1-D 数据落盘回放的编辑器菜单。
    ///
    /// 【为什么这些操作做成菜单而不是运行时按钮】
    ///   录制与回放都要**跨 Play 会话**才有意义：录完一个会话，改天再回放。
    ///   所以「选择录像文件」「打开录像文件夹」这类操作放在编辑器菜单里最顺手。
    ///   运行时开关键（开始/停止录制）则挂在 TwinRecorder 组件自身的 Inspector 上。
    ///
    /// 【菜单编号从 ⑫ 开始】
    ///   ① ~ ⑦ 是 P1TwinBootstrap，⑧ ~ ⑪ 是 TwinStressGen，这里接着往下排，
    ///   保持同一套「菜单即文档」的风格。
    ///
    /// 【⚠️ 本文件属于编辑器程序集，踩过的坑记在这里】
    ///   `Assets/_Project/Editor/**` 下的代码是 **Assembly-CSharp-Editor**，
    ///   而 `Assets/_Project/Scripts/**` 是 **Assembly-CSharp** —— 两个不同程序集。
    ///   所以这里**不能**直接用 `MiniJson`（它是 `internal`，跨程序集不可见）。
    ///   解析统一走 <see cref="RecordingReader"/>（runtime 程序集里的 public 类）。
    ///
    ///   判断可见性别只看命名空间 —— 两边都叫 `SchoolTwin.Data`，
    ///   但分属两个程序集，`internal` 一样看不见。
    /// </summary>
    public static class TwinRecordTools
    {
        private const string MenuRoot = "归档/P1 孪生框架/";

        // ================================================================ ⑫ 打开录像文件夹

        [MenuItem(MenuRoot + "⑫ 打开录像文件夹（Recordings）", false, 40)]
        public static void OpenRecordingsFolder()
        {
            string dir = RecordingReader.RecordingsDirectory();

            if (!Directory.Exists(dir))
            {
                bool create = EditorUtility.DisplayDialog(
                    "还没有录像",
                    "录像文件夹还不存在：\n" + dir + "\n\n" +
                    "录制是运行时行为 —— 需要在场景里有 TwinRecorder 组件，并且按 Play。\n\n" +
                    "现在创建一个空文件夹吗？",
                    "创建", "取消");
                if (!create) return;
                Directory.CreateDirectory(dir);
            }

            EditorUtility.RevealInFinder(dir);
        }

        // ================================================================ ⑬ 列出录像

        [MenuItem(MenuRoot + "⑬ 列出所有录像文件", false, 41)]
        public static void ListRecordings()
        {
            string dir = RecordingReader.RecordingsDirectory();

            if (!Directory.Exists(dir))
            {
                EditorUtility.DisplayDialog("没有录像", "文件夹不存在：\n" + dir + "\n\n先做一次录制。", "知道了");
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.jsonl");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("读取失败", e.Message, "知道了");
                return;
            }

            if (files.Length == 0)
            {
                EditorUtility.DisplayDialog("没有录像", "文件夹是空的：\n" + dir, "知道了");
                return;
            }

            Array.Sort(files, delegate (string a, string b)
            {
                return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a));
            });

            StringBuilder sb = new StringBuilder(2048);
            sb.Append("共 ").Append(files.Length).Append(" 个录像，按时间倒序：\n\n");

            for (int i = 0; i < files.Length && i < 30; i++)
            {
                FileInfo fi = new FileInfo(files[i]);
                sb.Append(Path.GetFileName(files[i]))
                  .Append("    ")
                  .Append((fi.Length / 1024.0).ToString("0.#")).Append(" KB    ")
                  .Append(fi.LastWriteTime.ToString("MM-dd HH:mm"))
                  .Append('\n');
            }
            if (files.Length > 30) sb.Append("\n… 其余 ").Append(files.Length - 30).Append(" 个省略");

            sb.Append("\n\n把文件名填进场景里 TwinPlayer 组件的「录像路径」即可回放，");
            sb.Append("或者留空让它自动取最新的一个。");

            EditorUtility.DisplayDialog("录像文件列表", sb.ToString(), "好的");
        }

        // ================================================================ ⑭ 检验录像文件

        [MenuItem(MenuRoot + "⑭ 检验录像文件（只读，不加载到场景）", false, 42)]
        public static void InspectRecording()
        {
            string path = EditorUtility.OpenFilePanel("选择录像文件（.jsonl）",
                RecordingReader.RecordingsDirectory(), "jsonl");

            if (string.IsNullOrEmpty(path)) return;

            string error;
            RecordingReader.Summary sum = RecordingReader.Inspect(path, out error);

            string report;
            if (sum == null)
            {
                report = "检验失败：\n" + error;
                Debug.LogError("[Twin] 录像检验失败：" + error);
            }
            else
            {
                report = RecordingReader.Describe(sum);
                Debug.Log("[Twin] 录像检验结果：\n" + report);
            }

            EditorUtility.DisplayDialog("录像检验", report, "好的");
        }
    }
}
