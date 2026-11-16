#include <windows.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include "json.hpp"
#include <iostream>
#include <map>
using Microsoft::WRL::ComPtr;
using Json = nlohmann::json;
std::string utf8(const wchar_t *text) {
  if (!text)
    return {};
  int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
  std::string s(n, 0);
  WideCharToMultiByte(CP_UTF8, 0, text, -1, s.data(), n, nullptr, nullptr);
  if (!s.empty())
    s.pop_back();
  return s;
}
std::string stringProperty(IPropertyStore *store, REFPROPERTYKEY key) {
  PROPVARIANT value;
  PropVariantInit(&value);
  std::string result;
  if (SUCCEEDED(store->GetValue(key, &value)) && value.vt == VT_LPWSTR)
    result = utf8(value.pwszVal);
  PropVariantClear(&value);
  return result;
}
int main() {
  if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)))
    return 1;
  try {
    ComPtr<IMMDeviceEnumerator> enumerator;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                IID_PPV_ARGS(&enumerator))))
      throw std::runtime_error("Enumerator failed");
    ComPtr<IMMDeviceCollection> collection;
    if (FAILED(enumerator->EnumAudioEndpoints(eAll, DEVICE_STATE_ACTIVE, &collection)))
      throw std::runtime_error("Enumeration failed");
    UINT count = 0;
    collection->GetCount(&count);
    Json rows = Json::array();
    std::map<std::string, size_t> groups;
    for (UINT i = 0; i < count; ++i) {
      ComPtr<IMMDevice> device;
      collection->Item(i, &device);
      ComPtr<IPropertyStore> store;
      if (FAILED(device->OpenPropertyStore(STGM_READ, &store)))
        throw std::runtime_error("Property store failed");
      ComPtr<IMMEndpoint> endpoint;
      device.As(&endpoint);
      EDataFlow flow;
      endpoint->GetDataFlow(&flow);
      PROPVARIANT container;
      PropVariantInit(&container);
      std::string group;
      if (SUCCEEDED(store->GetValue(PKEY_Device_ContainerId, &container)) &&
          container.vt == VT_CLSID && container.puuid &&
          !IsEqualGUID(*container.puuid, GUID_NULL)) {
        wchar_t text[40]{};
        StringFromGUID2(*container.puuid, text, 40);
        group = utf8(text);
      }
      PropVariantClear(&container);
      if (!group.empty() && !groups.count(group))
        groups.emplace(group, groups.size() + 1);
      rows.push_back({{"name", stringProperty(store.Get(), PKEY_Device_FriendlyName)},
                      {"groupName", stringProperty(store.Get(), PKEY_DeviceInterface_FriendlyName)},
                      {"manufacturer", stringProperty(store.Get(), PKEY_Device_Manufacturer)},
                      {"description", stringProperty(store.Get(), PKEY_Device_DeviceDesc)},
                      {"flow", flow == eCapture ? "capture" : "render"},
                      {"group", group.empty() ? Json(nullptr) : Json(groups.at(group))}});
    }
    std::cout << Json{{"scope", "IMMDevice active endpoint property stores opened STGM_READ only. "
                                "No IAudioClient activation, stream or device settings. Raw "
                                "endpoint/container identifiers are not saved."},
                      {"groups", groups.size()},
                      {"devices", rows}}
                     .dump(2)
              << std::endl;
  } catch (const std::exception &e) {
    std::cerr << e.what() << std::endl;
    return 2;
  }
  CoUninitialize();
}
