// ImeCaretMoveHelper.cs
//
// mde (MarkDown インラインエディタ) の一部。
// 「見出しへの変換」「箇条書きでEnterして新しい項目を作る」など、キー入力に応じて
// ドキュメントを書き換え、そのままCaretPositionを移動する処理すべてに共通する、
// IME（日本語入力）の変換候補ポップアップが消えずに固まってしまう不具合
// （DESIGN.md 14.12参照）への対策をまとめた共通処理。
//
// 当初はListEditorの中だけに書かれていたが、見出し変換（HeadingCodeBlockEditor）でも
// 同じ症状が実機で確認されたため（DESIGN.md 14.12 追記9参照）、共通処理として独立させた。
// ドキュメントを書き換えるすべての箇所がこの不具合の対象になり得るため、新しく同種の
// 処理を書く時は、この ScheduleCaretMove を使うことを検討すること。
//
// 【重要な注意】この対策は、症状の発生確率を下げることはできても、100%解消する保証は
// ない。実機での調査から、Windows側のIME（TSF）内部のタイミングに依存する、本質的に
// タイミング依存の競合状態（レースコンディション）である可能性が高いと考えられている。

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

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
        /// 箇条書きなど、症状が起きやすい状況ほど大きい値を渡す（DESIGN.md 14.12 追記7参照）。</param>
        /// <param name="a_cycleFocus">true（既定）なら、従来どおり
        /// Keyboard.ClearFocus()→a_editor.Focus()でフォーカスを当て直す。false なら、
        /// このフォーカスの当て直しだけを省略する（UpdateLayout・TryClearSuggestedXは行う）。
        /// 追記14で、ネストした箇条書き（2段目以降）に限ってfalseにする実験を行ったが、
        /// 追記15の通り症状は変わらず再現したため、この引数自体は症状の原因ではないと
        /// 判断し、既定のtrueに統一した（DESIGN.md 14.12 追記15参照）。</param>
        /// <param name="a_toggleInputMethod">true なら、moveCaretAction実行後に
        /// `InputMethod.SetIsInputMethodEnabled`を一度falseにしてからtrueに戻し、
        /// このRichTextBoxに対するTSF（Text Services Framework）側の入力コンテキストの
        /// 関連付けを強制的に張り直させる。既定はfalse（何もしない）。
        /// 追記15（DESIGN.md 14.12参照）：フォーカスの当て直し（ClearFocus/Focus）を省略しても
        /// 症状が変わらなかったことから、WPFの論理フォーカスそのものではなく、TSF側が保持する
        /// 「このテキストボックスの、今のドキュメント上のどこを見ているか」という内部状態が、
        /// ネストしたListの構造変更によって古くなったまま取り残されている可能性を疑っての実験。
        /// 追記18の通り、IMEをオンにした有効なテストでも症状を安定して防げなかったため、
        /// 既定はfalseに戻している（呼び出し元でも指定しない限り、この処理は実行されない）。</param>
        /// <param name="a_reassociateImeContext">true なら、moveCaretAction実行後に
        /// Win32の`ImmAssociateContextEx`（imm32.dll）を使い、このRichTextBoxを含む
        /// トップレベルウィンドウのIME入力コンテキストを一度切り離してから、既定の
        /// コンテキストに張り直す。既定はfalse（何もしない）。
        /// 追記18（DESIGN.md 14.12参照）：WPFの公開API（`InputMethod.SetIsInputMethodEnabled`）
        /// による対策では、IMEをオンにした有効なテストでも症状を安定して防げなかった。
        /// `InputMethod`はWPF内部のTextServicesHost経由の処理であり、Win32のIME入力
        /// コンテキストの関連付けそのもの（`ImmAssociateContext`系API）とは別の層である
        /// 可能性があるため、より低いレベルで入力コンテキストを張り直す対策として追加した。</param>
        public static void ScheduleCaretMove(
            RichTextBox a_editor,
            Action a_moveCaretAction,
            int a_rounds = 1,
            bool a_cycleFocus = true,
            bool a_toggleInputMethod = false,
            bool a_reassociateImeContext = false)
        {
            DebugLogger.Log(
                $"ScheduleCaretMove: 予約 rounds={a_rounds} cycleFocus={a_cycleFocus} " +
                $"toggleInputMethod={a_toggleInputMethod} reassociateImeContext={a_reassociateImeContext}");
            a_editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                DebugLogger.Log(
                    $"ScheduleCaretMove: コールバック開始 rounds={a_rounds} " +
                    $"IsFocused={a_editor.IsFocused} IsKeyboardFocused={a_editor.IsKeyboardFocused} " +
                    $"FocusedElement={Keyboard.FocusedElement}");
                if (a_rounds > 1)
                {
                    ScheduleCaretMove(a_editor, a_moveCaretAction, a_rounds - 1, a_cycleFocus, a_toggleInputMethod, a_reassociateImeContext);
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

                    if (a_toggleInputMethod)
                    {
                        TryToggleInputMethod(a_editor);
                    }

                    if (a_reassociateImeContext)
                    {
                        TryReassociateImeContext(a_editor);
                    }
                }
            }));
        }

        [DllImport("imm32.dll")]
        private static extern bool ImmAssociateContextEx(IntPtr a_hWnd, IntPtr a_hIMC, uint a_flags);

        // ImmAssociateContextExのdwFlags値（imm.hより）。
        // IACE_DEFAULT: hIMCを無視し、ウィンドウ（のクラス／スレッド）の既定のIMEコンテキストを
        // 関連付け直す。IACE_IGNORENOCONTEXT: hIMC(=NULL)を関連付け、実質的にIMEコンテキストを
        // 切り離す。
        private const uint IACE_DEFAULT = 0x0010;
        private const uint IACE_IGNORENOCONTEXT = 0x0020;

        /// <summary>
        /// 対象のRichTextBoxを含むトップレベルウィンドウについて、Win32の
        /// `ImmAssociateContextEx`（imm32.dll）を使い、IME入力コンテキストの関連付けを
        /// 一度切り離してから、既定のコンテキストに張り直す。
        /// `System.Windows.Input.InputMethod`（WPF内部のTextServicesHost経由）による
        /// 対策では、IMEをオンにした有効なテストでも症状を安定して防げなかったため
        /// （DESIGN.md 14.12 追記18参照）、より低いレベル（Win32のIMM32互換レイヤー、
        /// 最終的にはTSFへ委譲される）で入力コンテキストの関連付けそのものを張り直す。
        /// WPFはウィンドウ全体で1つのHWNDしか持たないため、対象はRichTextBox個別ではなく、
        /// それを含むトップレベルウィンドウになる（同時にキーボードフォーカスを持てるのは
        /// 1要素だけなので、実害はないはず）。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public static void TryReassociateImeContext(RichTextBox a_editor)
        {
            try
            {
                var hwndSource = PresentationSource.FromVisual(a_editor) as HwndSource;
                if (null == hwndSource)
                {
                    DebugLogger.Log("TryReassociateImeContext: HwndSourceが取得できなかった");
                    return;
                }
                IntPtr hWnd = hwndSource.Handle;
                DebugLogger.Log($"TryReassociateImeContext: 開始 hWnd=0x{hWnd.ToInt64():X}");
                bool disassociateOkFlg = ImmAssociateContextEx(hWnd, IntPtr.Zero, IACE_IGNORENOCONTEXT);
                bool reassociateOkFlg = ImmAssociateContextEx(hWnd, IntPtr.Zero, IACE_DEFAULT);
                DebugLogger.Log($"TryReassociateImeContext: 完了 disassociateOk={disassociateOkFlg} reassociateOk={reassociateOkFlg}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"TryReassociateImeContext: 例外 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 対象のRichTextBoxに対して、`InputMethod.SetIsInputMethodEnabled`を一度falseに
        /// してからtrueに戻す。目的はTSF（Text Services Framework）側がこの要素に対して
        /// 保持している入力コンテキストの関連付けを一度切り離し、張り直させることで、
        /// ネストしたListの構造変更後に古い状態が残る可能性を減らすこと（DESIGN.md 14.12
        /// 追記15参照）。`InputMethod`は非公開APIではなく、WPFの公開クラス
        /// （`System.Windows.Input.InputMethod`）なので、リフレクションは使わない。
        /// </summary>
        /// <param name="a_editor">対象のRichTextBox。</param>
        public static void TryToggleInputMethod(RichTextBox a_editor)
        {
            try
            {
                bool wasEnabledFlg = InputMethod.GetIsInputMethodEnabled(a_editor);
                DebugLogger.Log($"TryToggleInputMethod: 開始 wasEnabled={wasEnabledFlg}");
                InputMethod.SetIsInputMethodEnabled(a_editor, false);
                a_editor.UpdateLayout();
                InputMethod.SetIsInputMethodEnabled(a_editor, wasEnabledFlg);
                DebugLogger.Log("TryToggleInputMethod: 完了");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"TryToggleInputMethod: 例外 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// WPFの内部（非公開）APIである`TextEditorSelection._ClearSuggestedX`を、リフレクション
        /// 経由でベストエフォートで呼び出す。dotnet/wpfのIssue #10151（CaretPositionをコードから
        /// 設定すると、TextEditorの内部状態が正しく初期化されないことがある）が、IME固まり症状にも
        /// 関係している可能性を疑っての対策（DESIGN.md 14.12 追記8参照）。非公開APIへの
        /// リフレクションアクセスのため、.NET/WPFのバージョンが変われば型名・メンバー名ごと
        /// 失われる可能性があり、その場合は例外を握りつぶして何もしない
        /// （＝この呼び出しを省略したのと同じ状態に留まる）。
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
