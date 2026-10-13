using System;
using System.ComponentModel;
using Exiled.API.Interfaces;

namespace SeedGuard
{
    public sealed class Config : IConfig
    {
        public bool IsEnabled { get; set; } = true;
        public bool Debug { get; set; }
        [Description("玩家在控制台输入 seed 时显示的数字。客户端只能显示数字，无法显示文字；设为 0 可明确表示已隐藏（真实种子不可能是 0）。")]
        public int DisplayedSeed { get; set; } = 114514;
        [Description("隐藏种子后推送到玩家控制台的提示，玩家输入 .seed 时也回复这句话；留空则不推送。")]
        public string Notice { get; set; } = "服务器禁止使用Seed指令";
        [Description("控制台提示颜色，例如 red、yellow、green、cyan、magenta、white。")]
        public string NoticeColor { get; set; } = "red";
        [Description("隐藏种子后延迟多少秒推送控制台提示（0–60）。")]
        public float NoticeDelay { get; set; } = 3f;
        [Description("检查新玩家的间隔秒数（0.05–5）。")]
        public float CheckInterval { get; set; } = .25f;
        [Description("是否为每名玩家记录一条“已隐藏种子”日志。")]
        public bool LogEachPlayer { get; set; }

        internal void Validate()
        {
            if (Notice != null && Notice.Length > 300) throw new ArgumentException("Notice 不能超过 300 个字符。");
            if (string.IsNullOrEmpty(NoticeColor) || NoticeColor.Length > 16) throw new ArgumentException("NoticeColor 须为 1–16 个英文字母。");
            foreach (char c in NoticeColor)
                if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z')) throw new ArgumentException("NoticeColor 须为 1–16 个英文字母。");
            Check(NoticeDelay, 0, 60, nameof(NoticeDelay));
            Check(CheckInterval, .05f, 5, nameof(CheckInterval));
        }

        internal GuardSettings ToSettings() => new GuardSettings
        {
            DisplayedSeed = DisplayedSeed,
            Notice = Notice ?? string.Empty,
            NoticeColor = NoticeColor.ToLowerInvariant(),
            NoticeDelayMs = (long)Math.Round(NoticeDelay * 1000),
            CheckIntervalMs = (long)Math.Round(CheckInterval * 1000),
            LogEachPlayer = LogEachPlayer,
        };

        private static void Check(float value, float min, float max, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentException($"{name} 须在 {min}–{max} 之间。");
        }
    }
}
