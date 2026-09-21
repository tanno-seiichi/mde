// ImeCaretMoveHelper.cs
//
// mde (Markdown インラインエディタ) の一部。
// 「見出しへの変換」「箇条書きでEnter」など、ドキュメントを書き換えてCaretPositionを
// 移動する処理に共通する、IME変換候補ポップアップが消えず固まる不具合への対策。
// ドキュメントを書き換える箇所は全てこの不具合の対象になり得るため、同種の処理を
// 新しく書く時は ScheduleCaretMove の利用を検討すること。
//
// 【重要】この対策は発生確率を下げるのみで100%の解消は保証しない。Windows側IME(TSF)
// 内部のタイミングに依存する競合状態（レースコンディション）である可能性が高い。

using mde.logger;
using System;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;

namespace mde.common
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
        /// WPF内部の非公開API `TextEditorSelection._ClearSuggestedX` をリフレクションで呼び出す。
        /// コードからCaretPositionを設定するとTextEditorの内部状態が正しく初期化されないことが
        /// あり（dotnet/wpf Issue #10151）、これをクリアしてIME固まりを防ぐ。非公開APIのため
        /// .NET/WPFのバージョンが変わると型名・メンバー名ごと失敗し得るため、その場合は例外を
        /// 握りつぶして何もしない。
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
                // 非公開APIの構造が想定と異なる場合（.NET/WPFのバージョン差異等）は諦めて
                // 何もしない。呼び出し元の処理は続行する。
                DebugLogger.Log($"TryClearSuggestedX: 例外 {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
