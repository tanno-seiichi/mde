// ImeCaretMoveHelper.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 「見出しへの変換」「箇条書きでEnterして新しい項目を作る」など、キー入力に応じて
// ドキュメントを書き換え、そのままCaretPositionを移動する処理に共通する、IME
// （日本語入力）の変換候補ポップアップが消えずに固まってしまう不具合への対策をまとめた
// 共通処理。ドキュメントを書き換えるすべての箇所がこの不具合の対象になり得るため、
// 新しく同種の処理を書く時は、この ScheduleCaretMove を使うことを検討すること。
//
// 【重要な注意】この対策は、症状の発生確率を下げることはできても、100%解消する保証は
// ない。Windows側のIME（TSF）内部のタイミングに依存する、本質的にタイミング依存の
// 競合状態（レースコンディション）である可能性が高いと考えられている。

using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;

namespace mde
{
    /// <summary>
    /// IME固まり対策として、CaretPositionの変更・フォーカスの当て直しを1テンポ（以上）
    /// 遅らせるための共通処理。
    /// </summary>
    public static class ImeCaretMoveHelper
    {
        /// <summary>
        /// 指定したキャレット移動処理を、Dispatcher.BeginInvokeで1テンポ（以上）遅らせてから
        /// 実行する。実行の直前にUpdateLayout・WPF内部状態のリセット（TryClearSuggestedX）・
        /// フォーカスの当て直し（ClearFocus→Focus）もあわせて行う。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        /// <param name="a_moveCaretAction">実際にCaretPositionを変更する処理
        /// （すでに存在するTextPointer/Paragraphを参照するだけの、軽い処理であること）。</param>
        /// <param name="a_rounds">1テンポ遅らせる処理を何回重ねるか（既定1回）。ネストした
        /// 箇条書きなど、症状が起きやすい状況ほど大きい値を渡す。</param>
        /// <param name="a_cycleFocus">true（既定）なら、Keyboard.ClearFocus()→a_editor.Focus()で
        /// フォーカスを当て直す。false ならこの当て直しだけを省略する
        /// （UpdateLayout・TryClearSuggestedXは行う）。</param>
        public static void ScheduleCaretMove(
            RichTextBox a_editor,
            Action a_moveCaretAction,
            int a_rounds = 1,
            bool a_cycleFocus = true)
        {
            DebugLogger.Log($"ScheduleCaretMove: 予約 rounds={a_rounds} cycleFocus={a_cycleFocus}");
            a_editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                DebugLogger.Log(
                    $"ScheduleCaretMove: コールバック開始 rounds={a_rounds} " +
                    $"IsFocused={a_editor.IsFocused} IsKeyboardFocused={a_editor.IsKeyboardFocused} " +
                    $"FocusedElement={Keyboard.FocusedElement}");
                if (a_rounds > 1)
                {
                    ScheduleCaretMove(a_editor, a_moveCaretAction, a_rounds - 1, a_cycleFocus);
                }
                else
                {
                    DebugLogger.Log("ScheduleCaretMove: moveCaretAction実行前");
                    a_moveCaretAction();
                    DebugLogger.Log(
                        $"ScheduleCaretMove: moveCaretAction実行後 " +
                        $"IsFocused={a_editor.IsFocused} IsKeyboardFocused={a_editor.IsKeyboardFocused}");
                    a_editor.UpdateLayout();
                    DebugLogger.Log("ScheduleCaretMove: UpdateLayout完了");
                    TryClearSuggestedX(a_editor);
                    if (a_cycleFocus)
                    {
                        DebugLogger.Log(
                            $"ScheduleCaretMove: ClearFocus前 IsFocused={a_editor.IsFocused} " +
                            $"IsKeyboardFocused={a_editor.IsKeyboardFocused} FocusedElement={Keyboard.FocusedElement}");
                        Keyboard.ClearFocus();
                        DebugLogger.Log(
                            $"ScheduleCaretMove: ClearFocus後 IsFocused={a_editor.IsFocused} " +
                            $"IsKeyboardFocused={a_editor.IsKeyboardFocused} FocusedElement={Keyboard.FocusedElement}");
                        a_editor.Focus();
                        DebugLogger.Log(
                            $"ScheduleCaretMove: Focus後 IsFocused={a_editor.IsFocused} " +
                            $"IsKeyboardFocused={a_editor.IsKeyboardFocused} FocusedElement={Keyboard.FocusedElement}");
                    }
                    else
                    {
                        DebugLogger.Log(
                            $"ScheduleCaretMove: cycleFocus=falseのためClearFocus/Focusは省略 " +
                            $"IsFocused={a_editor.IsFocused} IsKeyboardFocused={a_editor.IsKeyboardFocused} " +
                            $"FocusedElement={Keyboard.FocusedElement}");
                    }
                }
            }));
        }

        /// <summary>
        /// WPFの内部（非公開）APIである`TextEditorSelection._ClearSuggestedX`を、リフレクション
        /// 経由でベストエフォートで呼び出す。CaretPositionをコードから設定すると、TextEditorの
        /// 内部状態が正しく初期化されないことがある（dotnet/wpfのIssue #10151）ため、これを
        /// クリアしてIME固まり症状を防ぐ。非公開APIへのリフレクションアクセスのため、.NET/WPFの
        /// バージョンが変われば型名・メンバー名ごと失われる可能性があり、その場合は例外を
        /// 握りつぶして何もしない（＝この呼び出しを省略したのと同じ状態に留まる）。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public static void TryClearSuggestedX(RichTextBox a_editor)
        {
            try
            {
                var textBoxBaseType = typeof(System.Windows.Controls.Primitives.TextBoxBase);
                var textEditorField = textBoxBaseType.GetField("_textEditor", BindingFlags.NonPublic | BindingFlags.Instance);
                object textEditor = textEditorField?.GetValue(a_editor);
                if (null == textEditor)
                {
                    DebugLogger.Log("TryClearSuggestedX: _textEditorフィールドが取得できなかった（型/バージョン差異の可能性）");
                    return;
                }

                Type textEditorSelectionType = textEditor.GetType().Assembly.GetType("System.Windows.Documents.TextEditorSelection");
                MethodInfo clearMethod = textEditorSelectionType?.GetMethod("_ClearSuggestedX", BindingFlags.NonPublic | BindingFlags.Static);
                if (null == clearMethod)
                {
                    DebugLogger.Log("TryClearSuggestedX: _ClearSuggestedXメソッドが取得できなかった（型/バージョン差異の可能性）");
                    return;
                }
                clearMethod.Invoke(null, new object[] { textEditor });
                DebugLogger.Log("TryClearSuggestedX: 成功");
            }
            catch (Exception ex)
            {
                // 非公開APIの内部構造が想定と違った場合（.NET/WPFのバージョン差異等）は、
                // このベストエフォートの対策を諦めるだけでよい。呼び出し元の処理は続行する。
                DebugLogger.Log($"TryClearSuggestedX: 例外 {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
