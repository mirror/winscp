//---------------------------------------------------------------------------
#include <vcl.h>
#pragma hdrstop

#include "WinInterface.h"
#include "VCLCommon.h"

#include <Common.h>
#include <TextsWin.h>
#include <RemoteFiles.h>

#include <FileCtrl.hpp>
#include <XPThemes.hpp>
//---------------------------------------------------------------------------
#pragma package(smart_init)
//---------------------------------------------------------------------------
void __fastcall AdjustListColumnsWidth(TListView* ListView)
{
  int OriginalWidth, NewWidth, i, CWidth;

  OriginalWidth = 0;
  for (i = 0; i < ListView->Columns->Count; i++)
  {
    OriginalWidth += ListView->Columns->Items[i]->Width;
  }

  NewWidth = 0;
  CWidth = ListView->ClientWidth;
  if ((ListView->VisibleRowCount < ListView->Items->Count) &&
      (ListView->Width - ListView->ClientWidth < GetSystemMetrics(SM_CXVSCROLL)))
  {
    CWidth -= GetSystemMetrics(SM_CXVSCROLL);
  }
  for (i = 0; i < ListView->Columns->Count-1;i++)
  {
    if (ListView->Columns->Items[i]->Tag == 0)
    {
      ListView->Columns->Items[i]->Width =
        (CWidth * ListView->Columns->Items[i]->Width) / OriginalWidth;
    }
    NewWidth += ListView->Columns->Items[i]->Width;
  }
  ListView->Columns->Items[ListView->Columns->Count-1]->Width = CWidth-NewWidth;
}
//---------------------------------------------------------------------------
void __fastcall EnableControl(TControl * Control, bool Enable)
{
  if (Control->Enabled != Enable)
  {
    if (Control->InheritsFrom(__classid(TWinControl)) &&
        (((TWinControl*)Control)->ControlCount > 0))
    {
      for (Integer Index = 0; Index < ((TWinControl*)Control)->ControlCount; Index++)
        EnableControl(((TWinControl*)Control)->Controls[Index], Enable);
    }
    Control->Enabled = Enable;
  }
  if (Control->InheritsFrom(__classid(TCustomEdit)) ||
            Control->InheritsFrom(__classid(TCustomComboBox)))
  {
    if (Enable) ((TEdit*)Control)->Color = clWindow;
      else ((TEdit*)Control)->Color = clBtnFace;
  }
};
//---------------------------------------------------------------------------
struct TSavedSystemSettings
{
  AnsiString FontName;
  bool Flipped;
};
//---------------------------------------------------------------------------
// Settings that must be set as soon as possible.
void __fastcall UseSystemSettingsPre(TCustomForm * Control, void ** Settings)
{
  if (Settings)
  {
    TSavedSystemSettings * SSettings = new TSavedSystemSettings();
    *Settings = static_cast<void*>(SSettings);
    SSettings->FontName = Control->Font->Name;
  }
  assert(Control && Control->Font);
  Control->Font->Name = "MS Shell Dlg";

  if (Control->HelpKeyword.IsEmpty())
  {
    // temporary help keyword to enable F1 key in all forms
    Control->HelpKeyword = "start";
  }
};
//---------------------------------------------------------------------------
// Settings that must be set only after whole form is constructed
void __fastcall UseSystemSettingsPost(TCustomForm * Control, void * Settings)
{
  bool Flip;
  AnsiString FlipStr = LoadStr(FLIP_CHILDREN);
  Flip = !FlipStr.IsEmpty() && static_cast<bool>(StrToInt(FlipStr));

  if (Settings != NULL)
  {
    static_cast<TSavedSystemSettings*>(Settings)->Flipped = Flip;
  }
  
  if (Flip)
  {
    Control->FlipChildren(true);
  }

  ResetSystemSettings(Control);
};
//---------------------------------------------------------------------------
void __fastcall UseSystemSettings(TCustomForm * Control, void ** Settings)
{
  UseSystemSettingsPre(Control, Settings);
  UseSystemSettingsPost(Control, (Settings != NULL) ? *Settings : NULL);
};
//---------------------------------------------------------------------------
void __fastcall ResetSystemSettings(TCustomForm * Control)
{
  XPTheme->ShowFocus(Control);
  XPTheme->ShowAccelerators(Control);
}
//---------------------------------------------------------------------------
void __fastcall RevokeSystemSettings(TCustomForm * Control,
  void * Settings)
{
  assert(Settings);
  if (static_cast<TSavedSystemSettings*>(Settings)->Flipped)
  {
    Control->FlipChildren(true);
  }
  delete Settings;
};
//---------------------------------------------------------------------------
void __fastcall LinkLabel(TLabel * Label)
{
  Label->ParentFont = true;
  Label->Font->Style = Label->Font->Style << fsUnderline;
  Label->Font->Color = clBlue;
}
//---------------------------------------------------------------------------
class TPublicForm : public TForm
{
friend void __fastcall ShowAsModal(TForm * Form, void *& Storage);
friend void __fastcall HideAsModal(TForm * Form, void *& Storage);
};
//---------------------------------------------------------------------------
struct TShowAsModalStorage
{
  void * FocusWindowList;
  void * FocusActiveWindow;
};
//---------------------------------------------------------------------------
void __fastcall ShowAsModal(TForm * Form, void *& Storage)
{
  CancelDrag();
  if (GetCapture() != 0) SendMessage(GetCapture(), WM_CANCELMODE, 0, 0);
  ReleaseCapture();
  (static_cast<TPublicForm*>(Form))->FFormState << fsModal;

  TShowAsModalStorage * AStorage = new TShowAsModalStorage;

  AStorage->FocusActiveWindow = GetActiveWindow();

  AStorage->FocusWindowList = DisableTaskWindows(0);
  Form->Show();
  SendMessage(Form->Handle, CM_ACTIVATE, 0, 0);

  Storage = AStorage;
}
//---------------------------------------------------------------------------
void __fastcall HideAsModal(TForm * Form, void *& Storage)
{
  assert((static_cast<TPublicForm*>(Form))->FFormState.Contains(fsModal));
  TShowAsModalStorage * AStorage = static_cast<TShowAsModalStorage *>(Storage);
  Storage = NULL;

  SendMessage(Form->Handle, CM_DEACTIVATE, 0, 0);
  if (GetActiveWindow() != Form->Handle)
  {
    AStorage->FocusActiveWindow = 0;
  }
  Form->Hide();

  EnableTaskWindows(AStorage->FocusWindowList);

  if (AStorage->FocusActiveWindow != 0)
  {
    SetActiveWindow(AStorage->FocusActiveWindow);
  }

  (static_cast<TPublicForm*>(Form))->FFormState >> fsModal;

  delete AStorage;
}
//---------------------------------------------------------------------------
void __fastcall ReleaseAsModal(TForm * Form, void *& Storage)
{
  if (Storage != NULL)
  {
    HideAsModal(Form, Storage);
  }
}
//---------------------------------------------------------------------------
bool __fastcall SelectDirectory(AnsiString & Path, const AnsiString Prompt,
  bool PreserveFileName)
{
  bool Result;
  unsigned int ErrorMode;
  ErrorMode = SetErrorMode(SEM_NOOPENFILEERRORBOX | SEM_FAILCRITICALERRORS);

  try
  {
    AnsiString Directory;
    AnsiString FileName;
    if (!PreserveFileName || DirectoryExists(Path))
    {
      Directory = Path;
    }
    else
    {
      Directory = ExtractFilePath(Path);
      FileName = ExtractFileName(Path);
    }
    Result = SelectDirectory(Prompt, "", Directory);
    if (Result)
    {
      Path = Directory;
      if (!FileName.IsEmpty())
      {
        Path = IncludeTrailingBackslash(Path) + FileName;
      }
    }
  }
  __finally
  {
    SetErrorMode(ErrorMode);
  }

  return Result;
}
//---------------------------------------------------------------------------
bool __fastcall ListViewAnyChecked(TListView * ListView, bool Checked)
{
  bool AnyChecked = false;
  for (int Index = 0; Index < ListView->Items->Count; Index++)
  {
    if (ListView->Items->Item[Index]->Checked == Checked)
    {
      AnyChecked = true;
      break;
    }
  }
  return AnyChecked;
}
//---------------------------------------------------------------------------
void __fastcall ListViewCheckAll(TListView * ListView,
  TListViewCheckAll CheckAll)
{
  bool Check;

  if (CheckAll == caToggle)
  {
    Check = ListViewAnyChecked(ListView, false);
  }
  else
  {
    Check = (CheckAll == caCheck);
  }

  for (int Index = 0; Index < ListView->Items->Count; Index++)
  {
    ListView->Items->Item[Index]->Checked = Check;
  }
}
//---------------------------------------------------------------------------
// Windows algorithm is as follows (tested on W2k):
// right:
//   is_delimiter(current)
//     false:
//       right(left(current) + 1)
//     true:
//       right(right(current) + 1)
// left:
//   right(left(current) + 1)
int CALLBACK PathWordBreakProc(char * Ch, int Current, int Len, int Code)
{
  char Delimiters[] = "\\/ ;,.";
  int Result;
  AnsiString ACh;
  // stupid unicode autodetection
  // (on WinXP (or rather for RichEdit 2.0) we get unicode on input)
  if ((Len > 1) && (Ch[1] == '\0'))
  {
    // this convertes the unicode to ansi
    ACh = (wchar_t*)Ch;
  }
  else
  {
    ACh = Ch;
  }
  if (Code == WB_ISDELIMITER)
  {
    // we return negacy of what WinAPI docs says
    Result = (strchr(Delimiters, ACh[Current + 1]) == NULL);
  }
  else if (Code == WB_LEFT)
  {
    Result = ACh.SubString(1, Current - 1).LastDelimiter(Delimiters);
  }
  else if (Code == WB_RIGHT)
  {
    if (Current == 0)
    {
      // will be called gain with Current == 1
      Result = 0;
    }
    else
    {
      const char * P = strpbrk(ACh.c_str() + Current - 1, Delimiters);
      if (P == NULL)
      {
        Result = Len;
      }
      else
      {
        Result = P - ACh.c_str() + 1;
      }
    }
  }
  else
  {
    assert(false);
    Result = 0;
  }
  return Result;
}
//---------------------------------------------------------------------------
class TPublicCustomCombo : public TCustomCombo
{
friend void __fastcall InstallPathWordBreakProc(TWinControl * Control);
};
//---------------------------------------------------------------------------
void __fastcall InstallPathWordBreakProc(TWinControl * Control)
{
  HWND Wnd;
  if (dynamic_cast<TCustomCombo*>(Control) != NULL)
  {
    TPublicCustomCombo * Combo =
      static_cast<TPublicCustomCombo *>(dynamic_cast<TCustomCombo *>(Control));
    Combo->HandleNeeded();
    Wnd = Combo->EditHandle;
  }
  else
  {
    Wnd = Control->Handle;
  }
  SendMessage(Wnd, EM_SETWORDBREAKPROC, 0, (LPARAM)(EDITWORDBREAKPROC)PathWordBreakProc);
}
//---------------------------------------------------------------------------
void __fastcall RepaintStatusBar(TCustomStatusBar * StatusBar)
{
  StatusBar->SimplePanel = !StatusBar->SimplePanel;
  StatusBar->SimplePanel = !StatusBar->SimplePanel;
}
//---------------------------------------------------------------------------
void __fastcall SetVerticalControlsOrder(TControl ** ControlsOrder, int Count)
{
  for (int Index = Count - 1; Index > 0; Index--)
  {
    if ((ControlsOrder[Index]->Top < ControlsOrder[Index - 1]->Top) &&
        ControlsOrder[Index - 1]->Visible)
    {
      ControlsOrder[Index]->Top = ControlsOrder[Index - 1]->Top +
        ControlsOrder[Index - 1]->Height;
      Index = Count;
    }
  }
}
//---------------------------------------------------------------------------
void __fastcall SetHorizontalControlsOrder(TControl ** ControlsOrder, int Count)
{
  for (int Index = Count - 1; Index > 0; Index--)
  {
    if ((ControlsOrder[Index]->Left < ControlsOrder[Index - 1]->Left) &&
        ControlsOrder[Index - 1]->Visible)
    {
      ControlsOrder[Index]->Left = ControlsOrder[Index - 1]->Left +
        ControlsOrder[Index - 1]->Width;
      Index = Count;
    }
  }
}
//---------------------------------------------------------------------------
TPoint __fastcall GetAveCharSize(TCanvas* Canvas)
{
  Integer I;
  Char Buffer[52];
  TSize Result;
  for (I = 0; I <= 25; I++) Buffer[I] = (Char)('A' + I);
  for (I = 0; I <= 25; I++) Buffer[I+26] = (Char)('a' + I);
  GetTextExtentPoint(Canvas->Handle, Buffer, 52, &Result);
  return TPoint(Result.cx / 52, Result.cy);
}
//---------------------------------------------------------------------------
void __fastcall MakeNextInTabOrder(TWinControl * Control, TWinControl * After)
{
  if (After->TabOrder > Control->TabOrder)
  {
    After->TabOrder = Control->TabOrder;
  }
  else if (After->TabOrder < Control->TabOrder - 1)
  {
    After->TabOrder = static_cast<TTabOrder>(Control->TabOrder - 1);
  }
}
