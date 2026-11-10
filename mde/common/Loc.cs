// Loc.cs
//
// mde (Markdown インラインエディタ) の一部。
// 画面の文字（メニュー・ボタン・ダイアログ・メッセージ等）を、日本語と英語で切り替える。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace mde.common
{
    /// <summary>
    /// 画面の文字の、日本語と英語の切り替え（メニュー「表示」→「言語」）。
    /// ・XAML の文字：日本語の文字を書いた要素に、英語の文字を Loc.En（ToolTip は Loc.EnToolTip）で添える
    /// 　（例：&lt;MenuItem Header="ファイル(_F)" common:Loc.En="_File"/&gt;）。英語にすると、Loc.En を添えた要素の文字を
    /// 　英語にし、日本語に戻すと、最初に書いてあった日本語の文字に戻す。どちらの言語でも、XAML を読めば両方の文字が
    /// 　分かるようにするため、この形にした。文字を入れる先は、要素の種類で決める（ApplyText）。
    /// ・コードで決める文字（メッセージボックス・状態によって変わるメニューの文字等）：Loc.T("日本語", "English") で、
    /// 　今の言語の文字を選ぶ。言語を切り替えた時に作り直す必要のある文字は、LanguageChanged で作り直す。
    /// ・デバッグログ（DebugLogger）の文字は、日本語のまま（調べる人のためのもので、画面には出さない）。
    /// </summary>
    public static class Loc
    {
        /// <summary>英語で表示しているか。</summary>
        public static bool IsEnglish { get; private set; }

        /// <summary>言語を切り替えた後に呼ばれる（コードで決める文字を作り直すため）。</summary>
        public static event Action LanguageChanged;

        /// <summary>今の言語の文字を返す。</summary>
        /// <param name="a_ja">日本語の文字。</param>
        /// <param name="a_en">英語の文字。</param>
        /// <returns>英語で表示している時は a_en、そうでなければ a_ja。</returns>
        public static string T(string a_ja, string a_en) => IsEnglish ? a_en : a_ja;

        /// <summary>
        /// 設定で言語が決まっていない時（初めて起動した時等）の言語。Windows の表示言語が日本語なら日本語、それ以外は英語。
        /// </summary>
        /// <returns>英語にするなら true。</returns>
        public static bool IsEnglishByDefault()
        {
            return !"ja".Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>言語を切り替える（Loc.En を添えたすべての要素の文字を入れ替え、LanguageChanged を呼ぶ）。</summary>
        /// <param name="a_englishFlg">英語にするなら true。</param>
        public static void SetLanguage(bool a_englishFlg)
        {
            bool changedFlg = IsEnglish != a_englishFlg;
            IsEnglish = a_englishFlg;
            for (int i = s_targets.Count - 1; 0 <= i; i--)
            {
                if (s_targets[i].TryGetTarget(out DependencyObject target))
                {
                    ApplyTo(target);
                }
                else
                {
                    s_targets.RemoveAt(i);
                }
            }
            if (changedFlg)
            {
                LanguageChanged?.Invoke();
            }
        }

        /// <summary>要素の英語の文字（Header・Text・Content・Title のうち、要素の種類で決まるもの）。</summary>
        public static readonly DependencyProperty EnProperty = DependencyProperty.RegisterAttached(
            "En", typeof(string), typeof(Loc), new PropertyMetadata(null, OnEnChanged));

        /// <summary>要素の英語のツールチップ。</summary>
        public static readonly DependencyProperty EnToolTipProperty = DependencyProperty.RegisterAttached(
            "EnToolTip", typeof(string), typeof(Loc), new PropertyMetadata(null, OnEnChanged));

        public static string GetEn(DependencyObject a_obj) => (string)a_obj.GetValue(EnProperty);
        public static void SetEn(DependencyObject a_obj, string a_value) => a_obj.SetValue(EnProperty, a_value);
        public static string GetEnToolTip(DependencyObject a_obj) => (string)a_obj.GetValue(EnToolTipProperty);
        public static void SetEnToolTip(DependencyObject a_obj, string a_value) => a_obj.SetValue(EnToolTipProperty, a_value);

        /// <summary>Loc.En・Loc.EnToolTip を添えた要素（ウィンドウを閉じた後に残らないよう、弱い参照で持つ）。</summary>
        private static readonly List<WeakReference<DependencyObject>> s_targets = new List<WeakReference<DependencyObject>>();

        /// <summary>要素の、最初に書いてあった日本語の文字。</summary>
        private sealed class JaTexts
        {
            public object Text;
            public object ToolTip;
        }

        /// <summary>要素ごとの日本語の文字（初めて英語にする時に覚える）。</summary>
        private static readonly ConditionalWeakTable<DependencyObject, JaTexts> s_jaTexts = new ConditionalWeakTable<DependencyObject, JaTexts>();

        /// <summary>
        /// Loc.En・Loc.EnToolTip が設定された時：要素を覚える。英語で表示している時は、英語にする。XAML を読み込む途中では、
        /// 日本語の文字（Header 等）がまだ設定されていないことがある（属性の順による）ため、読み込みが終わってから行う。
        /// </summary>
        private static void OnEnChanged(DependencyObject a_obj, DependencyPropertyChangedEventArgs a_args)
        {
            if (null == a_args.OldValue)
            {
                s_targets.Add(new WeakReference<DependencyObject>(a_obj));
            }
            if (IsEnglish)
            {
                a_obj.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyTo(a_obj)));
            }
        }

        /// <summary>要素の文字を、今の言語にする。</summary>
        private static void ApplyTo(DependencyObject a_obj)
        {
            JaTexts ja;
            if (!s_jaTexts.TryGetValue(a_obj, out ja))
            {
                if (!IsEnglish)
                {
                    return; // まだ一度も英語にしていない（日本語のまま）
                }
                ja = new JaTexts { Text = GetText(a_obj), ToolTip = (a_obj as FrameworkElement)?.ToolTip };
                s_jaTexts.Add(a_obj, ja);
            }
            string en = GetEn(a_obj);
            if (null != en)
            {
                SetText(a_obj, IsEnglish ? en : ja.Text);
            }
            string enToolTip = GetEnToolTip(a_obj);
            if (null != enToolTip && a_obj is FrameworkElement element)
            {
                element.ToolTip = IsEnglish ? enToolTip : ja.ToolTip;
            }
        }

        /// <summary>要素の種類で決まる、文字を入れる先の値を返す。</summary>
        private static object GetText(DependencyObject a_obj)
        {
            switch (a_obj)
            {
                case Window window: return window.Title;
                case HeaderedItemsControl headeredItems: return headeredItems.Header;
                case HeaderedContentControl headeredContent: return headeredContent.Header;
                case ContentControl content: return content.Content;
                case TextBlock textBlock: return textBlock.Text;
                case Run run: return run.Text;
                default: return null;
            }
        }

        /// <summary>要素の種類で決まる、文字を入れる先へ値を入れる（Window は Title、MenuItem は Header 等）。</summary>
        private static void SetText(DependencyObject a_obj, object a_value)
        {
            switch (a_obj)
            {
                case Window window: window.Title = a_value as string; break;
                case HeaderedItemsControl headeredItems: headeredItems.Header = a_value; break;
                case HeaderedContentControl headeredContent: headeredContent.Header = a_value; break;
                case ContentControl content: content.Content = a_value; break;
                case TextBlock textBlock: textBlock.Text = a_value as string; break;
                case Run run: run.Text = a_value as string; break;
            }
        }
    }
}
