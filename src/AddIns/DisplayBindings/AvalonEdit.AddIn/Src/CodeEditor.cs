// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <author name="Daniel Grunwald"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Bookmarks;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.AvalonEdit;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.Editor.Commands;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Integrates AvalonEdit with SharpDevelop.
	/// Also provides support for Split-View (showing two AvalonEdit instances using the same TextDocument)
	/// </summary>
	public class CodeEditor : Grid, IDisposable
	{
		const string contextMenuPath = "/SharpDevelop/ViewContent/AvalonEdit/ContextMenu";
		
		QuickClassBrowser quickClassBrowser;
		readonly CodeEditorView primaryTextEditor;
		readonly CodeEditorAdapter primaryTextEditorAdapter;
		CodeEditorView secondaryTextEditor;
		CodeEditorView activeTextEditor;
		CodeEditorAdapter secondaryTextEditorAdapter;
		GridSplitter gridSplitter;
		readonly IconBarManager iconBarManager;
		readonly TextMarkerService textMarkerService;
		ErrorPainter errorPainter;
		
		public CodeEditorView PrimaryTextEditor {
			get { return primaryTextEditor; }
		}
		
		public CodeEditorView ActiveTextEditor {
			get { return activeTextEditor; }
			private set {
				if (activeTextEditor != value) {
					activeTextEditor = value;
					HandleCaretPositionChange();
				}
			}
		}
		
		TextDocument document;
		
		public TextDocument Document {
			get {
				return document;
			}
			private set {
				if (document != value) {
					document = value;
					
					if (DocumentChanged != null) {
						DocumentChanged(this, EventArgs.Empty);
					}
				}
			}
		}
		
		public event EventHandler DocumentChanged;
		
		public IDocument DocumentAdapter {
			get { return primaryTextEditorAdapter.Document; }
		}
		
		public ITextEditor PrimaryTextEditorAdapter {
			get { return primaryTextEditorAdapter; }
		}
		
		public ITextEditor ActiveTextEditorAdapter {
			get { return this.ActiveTextEditor.Adapter; }
		}
		
		public IconBarManager IconBarManager {
			get { return iconBarManager; }
		}
		
		FileName fileName;
		
		public FileName FileName {
			get { return fileName; }
			set {
				if (fileName != value) {
					fileName = value;
					
					primaryTextEditorAdapter.FileNameChanged();
					if (secondaryTextEditorAdapter != null)
						secondaryTextEditorAdapter.FileNameChanged();
					
					if (this.errorPainter == null) {
						this.errorPainter = new ErrorPainter(primaryTextEditorAdapter);
					} else {
						this.errorPainter.UpdateErrors();
					}
					
					FetchParseInformation();
				}
			}
		}
		
		public void Redraw(ISegment segment, DispatcherPriority priority)
		{
			primaryTextEditor.TextArea.TextView.Redraw(segment, priority);
			if (secondaryTextEditor != null) {
				secondaryTextEditor.TextArea.TextView.Redraw(segment, priority);
			}
		}
		
		const double minRowHeight = 40;
		
		public CodeEditor()
		{
			CodeEditorOptions.Instance.PropertyChanged += CodeEditorOptions_Instance_PropertyChanged;
			CustomizedHighlightingColor.ActiveColorsChanged += CustomizedHighlightingColor_ActiveColorsChanged;
			
			this.CommandBindings.Add(new CommandBinding(SharpDevelopRoutedCommands.SplitView, OnSplitView));
			
			textMarkerService = new TextMarkerService(this);
			iconBarManager = new IconBarManager();
			
			primaryTextEditor = CreateTextEditor();
			primaryTextEditorAdapter = (CodeEditorAdapter)primaryTextEditor.TextArea.GetService(typeof(ITextEditor));
			Debug.Assert(primaryTextEditorAdapter != null);
			activeTextEditor = primaryTextEditor;
			
			this.Document = primaryTextEditor.Document;
			primaryTextEditor.SetBinding(TextEditor.DocumentProperty, new Binding("Document") { Source = this });
			
			this.ColumnDefinitions.Add(new ColumnDefinition());
			this.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = minRowHeight });
			SetRow(primaryTextEditor, 1);
			
			this.Children.Add(primaryTextEditor);
		}
		
		void CodeEditorOptions_Instance_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "EnableQuickClassBrowser")
				FetchParseInformation();
		}
		
		void CustomizedHighlightingColor_ActiveColorsChanged(object sender, EventArgs e)
		{
			// CustomizableHighlightingColorizer loads the new values automatically, we just need
			// to force a refresh in AvalonEdit.
			primaryTextEditor.TextArea.TextView.Redraw();
			if (secondaryTextEditor != null)
				secondaryTextEditor.TextArea.TextView.Redraw();
		}
		
		protected virtual CodeEditorView CreateTextEditor()
		{
			CodeEditorView textEditor = new CodeEditorView();
			CodeEditorAdapter adapter = new CodeEditorAdapter(this, textEditor);
			textEditor.Adapter = adapter;
			TextView textView = textEditor.TextArea.TextView;
			textView.Services.AddService(typeof(ITextEditor), adapter);
			textView.Services.AddService(typeof(CodeEditor), this);
			
			textEditor.TextArea.TextEntering += TextAreaTextEntering;
			textEditor.TextArea.TextEntered += TextAreaTextEntered;
			textEditor.TextArea.Caret.PositionChanged += TextAreaCaretPositionChanged;
			textEditor.TextArea.DefaultInputHandler.CommandBindings.Add(
				new CommandBinding(CustomCommands.CtrlSpaceCompletion, OnCodeCompletion));
			
			textView.BackgroundRenderers.Add(textMarkerService);
			textView.LineTransformers.Add(textMarkerService);
			textView.Services.AddService(typeof(ITextMarkerService), textMarkerService);
			
			textView.Services.AddService(typeof(IEditorUIService), new AvalonEditEditorUIService(textView));
			
			textView.Services.AddService(typeof(IBookmarkMargin), iconBarManager);
			textEditor.TextArea.LeftMargins.Insert(0, new IconBarMargin(iconBarManager));
			
			textView.Services.AddService(typeof(ISyntaxHighlighter), new AvalonEditSyntaxHighlighterAdapter(textView));
			
			textEditor.TextArea.MouseRightButtonDown += TextAreaMouseRightButtonDown;
			textEditor.TextArea.ContextMenuOpening += TextAreaContextMenuOpening;
			textEditor.TextArea.TextCopied += textEditor_TextArea_TextCopied;
			textEditor.GotFocus += textEditor_GotFocus;
			
			return textEditor;
		}
		
		public event EventHandler<TextEventArgs> TextCopied;

		void textEditor_TextArea_TextCopied(object sender, TextEventArgs e)
		{
			if (TextCopied != null)
				TextCopied(this, e);
		}

		protected virtual void DisposeTextEditor(TextEditor textEditor)
		{
			// detach IconBarMargin from IconBarManager
			textEditor.TextArea.LeftMargins.OfType<IconBarMargin>().Single().TextView = null;
		}
		
		void textEditor_GotFocus(object sender, RoutedEventArgs e)
		{
			Debug.Assert(sender is CodeEditorView);
			this.ActiveTextEditor = (CodeEditorView)sender;
		}
		
		void TextAreaContextMenuOpening(object sender, ContextMenuEventArgs e)
		{
			ITextEditor adapter = GetAdapterFromSender(sender);
			MenuService.ShowContextMenu(sender as UIElement, adapter, contextMenuPath);
		}
		
		void TextAreaMouseRightButtonDown(object sender, MouseButtonEventArgs e)
		{
			TextEditor textEditor = GetTextEditorFromSender(sender);
			var position = textEditor.GetPositionFromPoint(e.GetPosition(textEditor));
			if (position.HasValue) {
				textEditor.TextArea.Caret.Position = position.Value;
			}
		}
		
		// always use primary text editor for loading/saving
		// (the file encoding is stored only there)
		public void Load(Stream stream)
		{
			primaryTextEditor.Load(stream);
		}
		
		public void Save(Stream stream)
		{
			primaryTextEditor.Save(stream);
		}
		
		void OnSplitView(object sender, ExecutedRoutedEventArgs e)
		{
			if (secondaryTextEditor == null) {
				// create secondary editor
				this.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = minRowHeight });
				secondaryTextEditor = CreateTextEditor();
				secondaryTextEditorAdapter = (CodeEditorAdapter)secondaryTextEditor.TextArea.GetService(typeof(ITextEditor));
				Debug.Assert(primaryTextEditorAdapter != null);
				
				secondaryTextEditor.SetBinding(TextEditor.DocumentProperty,
				                               new Binding(TextEditor.DocumentProperty.Name) { Source = primaryTextEditor });
				secondaryTextEditor.SyntaxHighlighting = primaryTextEditor.SyntaxHighlighting;
				
				gridSplitter = new GridSplitter {
					Height = 4,
					HorizontalAlignment = HorizontalAlignment.Stretch,
					VerticalAlignment = VerticalAlignment.Top
				};
				SetRow(gridSplitter, 2);
				this.Children.Add(gridSplitter);
				
				secondaryTextEditor.Margin = new Thickness(0, 4, 0, 0);
				SetRow(secondaryTextEditor, 2);
				this.Children.Add(secondaryTextEditor);
				
				secondaryTextEditorAdapter.FileNameChanged();
				FetchParseInformation();
			} else {
				// remove secondary editor
				this.Children.Remove(secondaryTextEditor);
				this.Children.Remove(gridSplitter);
				DisposeTextEditor(secondaryTextEditor);
				secondaryTextEditor = null;
				secondaryTextEditorAdapter.Language.Detach();
				secondaryTextEditorAdapter = null;
				gridSplitter = null;
				this.RowDefinitions.RemoveAt(this.RowDefinitions.Count - 1);
				this.ActiveTextEditor = primaryTextEditor;
			}
		}
		
		public event EventHandler CaretPositionChanged;
		
		void TextAreaCaretPositionChanged(object sender, EventArgs e)
		{
			Debug.Assert(sender is Caret);
			Debug.Assert(!document.IsInUpdate);
			if (sender == this.ActiveTextEditor.TextArea.Caret) {
				HandleCaretPositionChange();
			}
		}
		
		void HandleCaretPositionChange()
		{
			if (quickClassBrowser != null) {
				quickClassBrowser.SelectItemAtCaretPosition(this.ActiveTextEditorAdapter.Caret.Position);
			}
			
			CaretPositionChanged.RaiseEvent(this, EventArgs.Empty);
		}
		
		volatile static ReadOnlyCollection<ICodeCompletionBinding> codeCompletionBindings;
		
		public static ReadOnlyCollection<ICodeCompletionBinding> CodeCompletionBindings {
			get {
				if (codeCompletionBindings == null) {
					codeCompletionBindings = AddInTree.BuildItems<ICodeCompletionBinding>("/AddIns/DefaultTextEditor/CodeCompletion", null, false).AsReadOnly();
				}
				return codeCompletionBindings;
			}
		}
		
		void TextAreaTextEntering(object sender, TextCompositionEventArgs e)
		{
			// don't start new code completion if there is still a completion window open
			if (completionWindow != null)
				return;
			
			if (e.Handled)
				return;
			
			ITextEditor adapter = GetAdapterFromSender(sender);
			
			foreach (char c in e.Text) {
				foreach (ICodeCompletionBinding cc in CodeCompletionBindings) {
					CodeCompletionKeyPressResult result = cc.HandleKeyPress(adapter, c);
					if (result == CodeCompletionKeyPressResult.Completed) {
						if (completionWindow != null) {
							// a new CompletionWindow was shown, but does not eat the input
							// tell it to expect the text insertion
							completionWindow.ExpectInsertionBeforeStart = true;
						}
						if (insightWindow != null) {
							insightWindow.ExpectInsertionBeforeStart = true;
						}
						return;
					} else if (result == CodeCompletionKeyPressResult.CompletedIncludeKeyInCompletion) {
						if (completionWindow != null) {
							if (completionWindow.StartOffset == completionWindow.EndOffset) {
								completionWindow.CloseWhenCaretAtBeginning = true;
							}
						}
						return;
					} else if (result == CodeCompletionKeyPressResult.EatKey) {
						e.Handled = true;
						return;
					}
				}
			}
		}
		
		void TextAreaTextEntered(object sender, TextCompositionEventArgs e)
		{
			if (e.Text.Length > 0 && !e.Handled) {
				ILanguageBinding languageBinding = GetAdapterFromSender(sender).Language;
				if (languageBinding != null && languageBinding.FormattingStrategy != null) {
					char c = e.Text[0];
					// When entering a newline, AvalonEdit might use either "\r\n" or "\n", depending on
					// what was passed to TextArea.PerformTextInput. We'll normalize this to '\n'
					// so that formatting strategies don't have to handle both cases.
					if (c == '\r')
						c = '\n';
					languageBinding.FormattingStrategy.FormatLine(GetAdapterFromSender(sender), c);
					
					if (c == '\n') {
						// Immediately parse on enter.
						// This ensures we have up-to-date CC info about the method boundary when a user
						// types near the end of a method.
						ParserService.BeginParse(this.FileName, this.DocumentAdapter.CreateSnapshot());
					}
				}
			}
		}
		
		ITextEditor GetAdapterFromSender(object sender)
		{
			ITextEditorComponent textArea = (ITextEditorComponent)sender;
			ITextEditor textEditor = (ITextEditor)textArea.GetService(typeof(ITextEditor));
			if (textEditor == null)
				throw new InvalidOperationException("could not find TextEditor service");
			return textEditor;
		}
		
		CodeEditorView GetTextEditorFromSender(object sender)
		{
			ITextEditorComponent textArea = (ITextEditorComponent)sender;
			CodeEditorView textEditor = (CodeEditorView)textArea.GetService(typeof(TextEditor));
			if (textEditor == null)
				throw new InvalidOperationException("could not find TextEditor service");
			return textEditor;
		}
		
		void OnCodeCompletion(object sender, ExecutedRoutedEventArgs e)
		{
			CloseExistingCompletionWindow();
			CodeEditorView textEditor = GetTextEditorFromSender(sender);
			foreach (ICodeCompletionBinding cc in CodeCompletionBindings) {
				if (cc.CtrlSpace(textEditor.Adapter)) {
					e.Handled = true;
					break;
				}
			}
		}
		
		SharpDevelopCompletionWindow completionWindow;
		SharpDevelopInsightWindow insightWindow;
		
		void CloseExistingCompletionWindow()
		{
			if (completionWindow != null) {
				completionWindow.Close();
			}
		}
		
		void CloseExistingInsightWindow()
		{
			if (insightWindow != null) {
				insightWindow.Close();
			}
		}
		
		public SharpDevelopCompletionWindow ActiveCompletionWindow {
			get { return completionWindow; }
		}
		
		public SharpDevelopInsightWindow ActiveInsightWindow {
			get { return insightWindow; }
		}
		
		internal void ShowCompletionWindow(SharpDevelopCompletionWindow window)
		{
			CloseExistingCompletionWindow();
			completionWindow = window;
			window.Closed += delegate {
				completionWindow = null;
			};
			Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(
				delegate {
					if (completionWindow == window) {
						window.Show();
					}
				}
			));
		}
		
		internal void ShowInsightWindow(SharpDevelopInsightWindow window)
		{
			CloseExistingInsightWindow();
			insightWindow = window;
			window.Closed += delegate {
				insightWindow = null;
			};
			Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(
				delegate {
					if (insightWindow == window) {
						window.Show();
					}
				}
			));
		}
		
		public IHighlightingDefinition SyntaxHighlighting {
			get { return primaryTextEditor.SyntaxHighlighting; }
			set {
				primaryTextEditor.SyntaxHighlighting = value;
				if (secondaryTextEditor != null) {
					secondaryTextEditor.SyntaxHighlighting = value;
				}
			}
		}
		
		void FetchParseInformation()
		{
			ParseInformation parseInfo = ParserService.GetExistingParseInformation(this.FileName);
			if (parseInfo == null) {
				// if parse info is not yet available, start parsing
				var future = ParserService.BeginParse(this.FileName, primaryTextEditorAdapter.Document);
				if (future.Wait(50))
					parseInfo = future.Result;
			}
			ParseInformationUpdated(parseInfo);
		}
		
		public void ParseInformationUpdated(ParseInformation parseInfo)
		{
			if (parseInfo != null && CodeEditorOptions.Instance.EnableQuickClassBrowser) {
				// don't create quickClassBrowser for files that don't have any classes
				// (but do keep the quickClassBrowser when the last class is removed from a file)
				if (quickClassBrowser != null || parseInfo.CompilationUnit.Classes.Count > 0) {
					if (quickClassBrowser == null) {
						quickClassBrowser = new QuickClassBrowser();
						quickClassBrowser.JumpAction = (line, col) => ActiveTextEditor.JumpTo(line, col);
						SetRow(quickClassBrowser, 0);
						this.Children.Add(quickClassBrowser);
					}
					quickClassBrowser.Update(parseInfo.CompilationUnit);
					quickClassBrowser.SelectItemAtCaretPosition(this.ActiveTextEditorAdapter.Caret.Position);
				}
			} else {
				if (quickClassBrowser != null) {
					this.Children.Remove(quickClassBrowser);
					quickClassBrowser = null;
				}
			}
			iconBarManager.UpdateClassMemberBookmarks(parseInfo);
			primaryTextEditor.UpdateParseInformation(parseInfo);
			if (secondaryTextEditor != null)
				secondaryTextEditor.UpdateParseInformation(parseInfo);
		}
		
		public void Dispose()
		{
			CodeEditorOptions.Instance.PropertyChanged -= CodeEditorOptions_Instance_PropertyChanged;
			CustomizedHighlightingColor.ActiveColorsChanged -= CustomizedHighlightingColor_ActiveColorsChanged;
			
			primaryTextEditorAdapter.Language.Detach();
			if (secondaryTextEditorAdapter != null)
				secondaryTextEditorAdapter.Language.Detach();
			
			if (errorPainter != null)
				errorPainter.Dispose();
			this.Document = null;
		}
	}
}