//---------------------------------------------------------------------------
#include <vcl.h>
#pragma hdrstop

#include <Common.h>
#include "CustomWinConfiguration.h"
//---------------------------------------------------------------------------
#pragma package(smart_init)
//---------------------------------------------------------------------------
__fastcall TCustomWinConfiguration::TCustomWinConfiguration():
  TGUIConfiguration()
{
  FCommandsHistory = new TStringList();
}
//---------------------------------------------------------------------------
__fastcall TCustomWinConfiguration::~TCustomWinConfiguration()
{
  delete FCommandsHistory;
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::Default()
{
  TGUIConfiguration::Default();

  FMaskHistory = "";
  FShowAdvancedLoginOptions = false;
  FInterface = ifCommander;
  FLogView = lvNone;

  FCommandsHistory->Clear();
  FCommandsHistoryModified = false;
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::ModifyAll()
{
  TGUIConfiguration::ModifyAll();
  FCommandsHistoryModified = true;
}
//---------------------------------------------------------------------------
// duplicated from core\configuration.cpp
#define LASTELEM(ELEM) \
  ELEM.SubString(ELEM.LastDelimiter(".>")+1, ELEM.Length() - ELEM.LastDelimiter(".>"))
#define BLOCK(KEY, CANCREATE, BLOCK) \
  if (Storage->OpenSubKey(KEY, CANCREATE)) try { BLOCK } __finally { Storage->CloseSubKey(); }
#define REGCONFIG(CANCREATE) \
  BLOCK("Interface", CANCREATE, \
    KEY(String,   MaskHistory); \
    KEY(Integer,  Interface); \
    KEY(Bool,     ShowAdvancedLoginOptions); \
  ) \
  BLOCK("Logging", CANCREATE, \
    KEY(Integer, LogView); \
  );
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SaveSpecial(
  THierarchicalStorage * Storage)
{
  TGUIConfiguration::SaveSpecial(Storage);

  // duplicated from core\configuration.cpp
  #define KEY(TYPE, VAR) Storage->Write ## TYPE(LASTELEM(AnsiString(#VAR)), VAR)
  REGCONFIG(true);
  #undef KEY

  if (FCommandsHistoryModified && Storage->OpenSubKey("Commands", true))
  {
    Storage->WriteValues(FCommandsHistory);
    Storage->CloseSubKey();
    FCommandsHistoryModified = false;
  }
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::LoadSpecial(
  THierarchicalStorage * Storage)
{
  TGUIConfiguration::LoadSpecial(Storage);

  // duplicated from core\configuration.cpp
  #define KEY(TYPE, VAR) VAR = Storage->Read ## TYPE(LASTELEM(AnsiString(#VAR)), VAR)
  #pragma warn -eas
  REGCONFIG(false);
  #pragma warn +eas
  #undef KEY

  if (Storage->OpenSubKey("Commands", false))
  {
    FCommandsHistory->Clear();
    Storage->ReadValues(FCommandsHistory);
    Storage->CloseSubKey();
  }
  else if (FCommandsHistoryModified)
  {
    FCommandsHistory->Clear();
  }
  FCommandsHistoryModified = false;

}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SetMaskHistory(AnsiString value)
{
  SET_CONFIG_PROPERTY(MaskHistory);
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SetShowAdvancedLoginOptions(bool value)
{
  SET_CONFIG_PROPERTY(ShowAdvancedLoginOptions);
}
//---------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SetLogView(TLogView value)
{
  SET_CONFIG_PROPERTY(LogView);
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SetInterface(TInterface value)
{
  SET_CONFIG_PROPERTY(Interface);
}
//---------------------------------------------------------------------------
void __fastcall TCustomWinConfiguration::SetCommandsHistory(TStrings * value)
{
  assert(FCommandsHistory);
  if (!FCommandsHistory->Equals(value))
  {
    FCommandsHistory->Assign(value);
    FCommandsHistoryModified = true;
  }
}
