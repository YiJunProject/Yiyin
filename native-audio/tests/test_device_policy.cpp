#include "device_policy.h"
#include <iostream>
int main(){
  int passed=0;validateDeviceGroup("same","same");++passed;
  for(auto pair:{std::pair<std::string,std::string>{"one","two"},{"","same"},{"same",""},{"",""}}){
    try{validateDeviceGroup(pair.first,pair.second);return 1;}catch(const std::runtime_error&){++passed;}
  }
  std::cout << "{\"passed\":"<<passed<<",\"total\":5,\"scope\":\"Pure production device-group policy; no endpoint opened\"}\n";
}
