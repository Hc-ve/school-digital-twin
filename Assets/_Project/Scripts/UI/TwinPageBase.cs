using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】抽屉里的一"页"。所有模块页都继承它。
    ///
    /// 约定：页面自己负责 Build 内容（纯代码搭 UGUI，和项目惯例一致），
    /// 页栈只负责显示/隐藏与标题。**页面不要自己 SetActive**，由页栈统一管。
    /// </summary>
    public abstract class TwinPageBase : MonoBehaviour
    {
        /// <summary>页标题，显示在抽屉头部。</summary>
        public abstract string Title { get; }

        /// <summary>页副标题（可选，显示在标题下方）。</summary>
        public virtual string Subtitle { get { return null; } }

        /// <summary>成为栈顶时调用。用来做"每次进入都刷新一次"。</summary>
        public virtual void OnShow() { }

        /// <summary>离开栈顶时调用。用来释放临时资源/停掉定时刷新。</summary>
        public virtual void OnHide() { }
    }
}
