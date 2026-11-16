#pragma once
#include "public.sdk/source/common/pluginview.h"
#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include <windows.h>
#include <algorithm>
#include <cstring>
class EditorFixtureView final : public Steinberg::CPluginView, public Steinberg::IPlugViewContentScaleSupport {
  HWND child = nullptr;
  float scale = 1;
public:
  EditorFixtureView() { rect={0,0,400,240}; }
  Steinberg::tresult PLUGIN_API isPlatformTypeSupported(Steinberg::FIDString type) override { return !strcmp(type,Steinberg::kPlatformTypeHWND)?Steinberg::kResultTrue:Steinberg::kResultFalse; }
  void attachedToParent() override {
    child=CreateWindowW(L"STATIC",L"Owned resizable VST3 DPI fixture",WS_CHILD|WS_VISIBLE|SS_CENTER,0,0,rect.getWidth(),rect.getHeight(),(HWND)systemWindow,nullptr,GetModuleHandle(nullptr),nullptr);
    SetPropW((HWND)systemWindow,L"FixtureDpiPercent",(HANDLE)(INT_PTR)(int)(scale*100));
  }
  void removedFromParent() override {if(child)DestroyWindow(child);child=nullptr;}
  Steinberg::tresult PLUGIN_API canResize() override {return Steinberg::kResultTrue;}
  Steinberg::tresult PLUGIN_API checkSizeConstraint(Steinberg::ViewRect *r) override {
    r->right=r->left+std::clamp(r->getWidth(),320,1200);r->bottom=r->top+std::clamp(r->getHeight(),180,900);return Steinberg::kResultTrue;
  }
  Steinberg::tresult PLUGIN_API onSize(Steinberg::ViewRect *r) override {
    CPluginView::onSize(r);if(child)SetWindowPos(child,nullptr,0,0,r->getWidth(),r->getHeight(),SWP_NOZORDER|SWP_NOACTIVATE);return Steinberg::kResultTrue;
  }
  Steinberg::tresult PLUGIN_API setContentScaleFactor(float factor) override {
    scale=factor;if(systemWindow)SetPropW((HWND)systemWindow,L"FixtureDpiPercent",(HANDLE)(INT_PTR)(int)(scale*100));
    Steinberg::ViewRect r{0,0,(int)(400*factor),(int)(240*factor)};if(plugFrame)plugFrame->resizeView(this,&r);else rect=r;return Steinberg::kResultTrue;
  }
  DEFINE_INTERFACES
    DEF_INTERFACE (Steinberg::IPlugViewContentScaleSupport)
  END_DEFINE_INTERFACES (Steinberg::CPluginView)
  REFCOUNT_METHODS (Steinberg::CPluginView)
};
