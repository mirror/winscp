//---------------------------------------------------------------------------
#ifndef CustomWinConfigurationH
#define CustomWinConfigurationH
//---------------------------------------------------------------------------
#include "GUIConfiguration.h"
//---------------------------------------------------------------------------
#define WM_LOCALE_CHANGE (WM_USER + 1)
//---------------------------------------------------------------------------
class TCustomWinConfiguration : public TGUIConfiguration
{
private:
  AnsiString FMaskHistory;
  TLogView FLogView;
  TInterface FInterface;
  bool FShowAdvancedLoginOptions;
  TStrings * FCommandsHistory;
  bool FCommandsHistoryModified;

  void __fastcall SetMaskHistory(AnsiString value);
  void __fastcall SetInterface(TInterface value);
  void __fastcall SetLogView(TLogView value);
  void __fastcall SetShowAdvancedLoginOptions(bool value);
  void __fastcall SetCommandsHistory(TStrings * value);

protected:
  virtual void __fastcall SaveSpecial(THierarchicalStorage * Storage);
  virtual void __fastcall LoadSpecial(THierarchicalStorage * Storage);
  virtual void __fastcall ModifyAll();

public:
  __fastcall TCustomWinConfiguration();
  __fastcall ~TCustomWinConfiguration();
  virtual void __fastcall Default();

  __property AnsiString MaskHistory = { read = FMaskHistory, write = SetMaskHistory };
  __property TLogView LogView = { read = FLogView, write = SetLogView };
  __property TInterface Interface = { read = FInterface, write = SetInterface };
  __property bool ShowAdvancedLoginOptions = { read = FShowAdvancedLoginOptions, write = SetShowAdvancedLoginOptions};
  __property TStrings * CommandsHistory = { read = FCommandsHistory, write = SetCommandsHistory };
};
//---------------------------------------------------------------------------
#define CustomWinConfiguration \
  (dynamic_cast<TCustomWinConfiguration *>(Configuration))
//---------------------------------------------------------------------------
#endif
