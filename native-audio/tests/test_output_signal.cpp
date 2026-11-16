#include "output_signal.h"
#include <cstdlib>
#include <iostream>
#include <limits>
#include <new>
#include <stdexcept>
static std::atomic<unsigned> allocations{0};
void *operator new(size_t size){++allocations;if(auto p=std::malloc(size))return p;throw std::bad_alloc();}
void operator delete(void*p)noexcept{std::free(p);}
void operator delete(void*p,size_t)noexcept{std::free(p);}
int main(){
 int checks=0;auto check=[&](bool value,const char*message){++checks;if(!value)throw std::runtime_error(message);};
 try{
  OutputSignal s;float input[]{1.5f,-1.75f,.5f,-.25f,1,-1,0,0};
  auto before=allocations.load();s.observe(input,4,2);
  check(allocations==before,"output observation allocates no memory");
  check(s.peakSinceReset==1.75f && s.lastBlockPeak==1.75f,"observes overscale final mix");
  check(s.overRangeSamples==2 && s.framesObserved==4,"counts interleaved overscale samples independently from frames");
  check(input[0]==1.5f && input[1]==-1.75f,"observer does not clamp or modify signal");
  float zero[8]{};s.observe(zero,4,2);
  check(s.lastBlockPeak==0 && s.peakSinceReset==1.75f && s.overRangeSamples==2,"quiet output clears current peak but retains diagnostic history");
  float invalid[]{std::numeric_limits<float>::infinity(),std::numeric_limits<float>::quiet_NaN()};s.observe(invalid,1,2);
  check(s.nonFiniteSamples==2 && s.overRangeSamples==2,"nonfinite arithmetic has a separate diagnosis from above-full-scale float");
  s.reset();check(s.peakSinceReset==0 && s.lastBlockPeak==0 && s.overRangeSamples==0 && s.nonFiniteSamples==0 && s.framesObserved==0,"stopped reset clears observations");
  std::cout<<"{\"passed\":true,\"checks\":"<<checks<<",\"hardwareOpened\":false}\n";
  return 0;
 }catch(const std::exception&e){std::cout<<"{\"passed\":false,\"checks\":"<<checks<<",\"error\":\""<<e.what()<<"\"}\n";return 1;}
}
