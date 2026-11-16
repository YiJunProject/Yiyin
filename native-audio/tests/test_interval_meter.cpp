#include "interval_meter.h"
#include <cstdlib>
#include <iostream>
#include <new>
#include <stdexcept>

static std::atomic<uint64_t> allocations{0};
void *operator new(size_t size) { ++allocations;if(auto p=std::malloc(size))return p;throw std::bad_alloc(); }
void operator delete(void*p)noexcept{std::free(p);}
void operator delete(void*p,size_t)noexcept{std::free(p);}
int main(){
  int checks=0;
  auto require=[&](bool v,const char*name){++checks;if(!v)throw std::runtime_error(name);};
  try{
    IntervalMeter meter;
    meter.publish(2.f,.5f,4,.25,64);
    for(int i=0;i<100;++i)meter.publish(0,0,0,0,64);
    require(meter.left.load()==0,"status keeps last block without consuming interval");
    auto burst=meter.take();
    require(burst.peakLeft==2 && burst.peakRight==.5f,"single sample burst survives 100 silent blocks");
    require(burst.sampleCount==6464 && std::abs(burst.rmsLeft()-std::sqrt(4./6464))<1e-14,"RMS includes silent frames exactly");
    auto empty=meter.take();require(empty.sampleCount==0&&empty.rmsLeft()==0&&empty.peakLeft==0,"empty interval is zero");
    meter.publish(.25f,.5f,16*.25*.25,16*.5*.5,16);
    meter.publish(.75f,1.5f,48*.75*.75,48*1.5*1.5,48);
    auto weighted=meter.take();require(std::abs(weighted.rmsLeft()-std::sqrt((16*.25*.25+48*.75*.75)/64))<1e-14,"RMS weights frames rather than block averages");
    auto before=allocations.load();for(int i=0;i<10000;++i)meter.publish(1,1,64,64,64);
    require(allocations.load()==before,"10000 callback meter updates allocate no memory");
    meter.take();
    std::atomic<bool> done{false},go{false};
    std::thread audio([&]{while(!go.load()){};for(int i=0;i<100000;++i)meter.publish(i%7==0?2.f:.25f,.5f,4.,16.,64);done=true;});
    uint64_t total=0;double squares=0;float peak=0;int reads=0;go=true;
    while(!done.load()) {auto s=meter.take();total+=s.sampleCount;squares+=s.squareLeft;peak=std::max(peak,s.peakLeft);++reads;}
    audio.join();auto tail=meter.take();total+=tail.sampleCount;squares+=tail.squareLeft;peak=std::max(peak,tail.peakLeft);
    require(total==6400000,"concurrent snapshots lose no frames");
    require(squares==400000.,"concurrent snapshots pair square sums and frame counts");
    require(peak==2,"concurrent snapshots retain overscale peaks");
    require(reads>0,"control reader overlapped audio producer");
    std::cout<<"{\"passed\":true,\"checks\":"<<checks<<",\"frames\":"<<total<<",\"concurrentReads\":"<<reads<<",\"hardwareOpened\":false}\n";
    return 0;
  }catch(const std::exception&e){std::cout<<"{\"passed\":false,\"checks\":"<<checks<<",\"error\":\""<<e.what()<<"\"}\n";return 1;}
}
