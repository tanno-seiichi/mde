// MdeRichTextBox.cs
//
// mde (Markdown インラインエディタ) の一部。
// 編集ペインの RichTextBox。UI オートメーション（画面読み上げ・他のアプリが画面の内容を読み取る仕組み）に、文書の中の
// 要素（段落・表・表のセル等）を個別の要素として見せないようにする。

using System.Collections.Generic;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace mde.editor
{
    /// <summary>
    /// 編集ペインの RichTextBox。
    /// WPF の RichTextBox は、UI オートメーションに文書の中の要素（段落・表・表のセル等）を子の要素として見せる。
    /// 他のアプリ（画面読み上げ、他のアプリの文字を読み取るアプリ等）がその要素の画面上の位置を問い合わせると、WPF は
    /// 表全体の位置を UI スレッドで計算するため、大きな表（数百行）では1回の問い合わせで 0.2〜0.5 秒止まり、問い合わせの
    /// たびに編集・スクロールが固まっていた。そこで、文書の中の要素は子の要素として見せないようにする。
    /// 文字の読み取り（TextPattern。選択範囲・文字の取得等）は、これまで通り使える（RichTextBoxAutomationPeer のまま）。
    /// </summary>
    public class MdeRichTextBox : RichTextBox
    {
        /// <summary>UI オートメーション用の要素を作る（文書の中の要素を子にしない MdeRichTextBoxAutomationPeer）。</summary>
        /// <returns>UI オートメーション用の要素。</returns>
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new MdeRichTextBoxAutomationPeer(this);
        }

        /// <summary>文書の中の要素（段落・表・表のセル等）を子の要素として見せない RichTextBoxAutomationPeer。</summary>
        private sealed class MdeRichTextBoxAutomationPeer : RichTextBoxAutomationPeer
        {
            /// <param name="a_owner">編集ペインの RichTextBox。</param>
            public MdeRichTextBoxAutomationPeer(RichTextBox a_owner) : base(a_owner)
            {
            }

            /// <summary>子の要素は無し（文書の中の要素を見せない。理由は MdeRichTextBox のコメント参照）。</summary>
            /// <returns>null（子の要素は無し）。</returns>
            protected override List<AutomationPeer> GetChildrenCore()
            {
                return null;
            }
        }
    }
}
