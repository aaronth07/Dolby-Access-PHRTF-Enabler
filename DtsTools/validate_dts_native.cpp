// Isolated, version-pinned SPAC validator. No audio, property or COM interfaces.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <vector>

static std::vector<unsigned char> read(const wchar_t* path) {
    std::ifstream stream(std::filesystem::path(path),std::ios::binary|std::ios::ate);
    if(!stream) throw std::runtime_error("Cannot open input file");
    auto size=stream.tellg();
    if(size<=0 || size>16*1024*1024) throw std::runtime_error("Unsupported file extent");
    std::vector<unsigned char> data(static_cast<size_t>(size));
    stream.seekg(0);
    if(!stream.read(reinterpret_cast<char*>(data.data()),size)) throw std::runtime_error("Incomplete input read");
    return data;
}

static std::string sha256(const std::vector<unsigned char>& bytes) {
    BCRYPT_ALG_HANDLE algorithm=nullptr;
    BCRYPT_HASH_HANDLE hash=nullptr;
    ULONG objectSize=0,received=0;
    std::vector<unsigned char> object;
    unsigned char digest[32];
    auto check=[](NTSTATUS status) { if(status<0) throw std::runtime_error("SHA-256 failed"); };
    try {
        check(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0));
        check(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&objectSize),sizeof(objectSize),&received,0));
        object.resize(objectSize);
        check(BCryptCreateHash(algorithm,&hash,object.data(),objectSize,nullptr,0,0));
        check(BCryptHashData(hash,const_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(bytes.size()),0));
        check(BCryptFinishHash(hash,digest,sizeof(digest),0));
    } catch(...) {
        if(hash) BCryptDestroyHash(hash);
        if(algorithm) BCryptCloseAlgorithmProvider(algorithm,0);
        throw;
    }
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm,0);
    std::ostringstream output;
    output<<std::hex<<std::setfill('0');
    for(unsigned char byte:digest) output<<std::setw(2)<<static_cast<unsigned>(byte);
    return output.str();
}

int wmain(int argc,wchar_t** argv) {
    HMODULE module=nullptr;
    try {
        if(argc!=3 && !(argc==4 && std::wstring(argv[3])==L"--device")) throw std::runtime_error("Usage: validate_dts_native.exe DLL BLOB [--device]");
        if(sizeof(void*)!=8) throw std::runtime_error("x64 build required");
        auto dllPath=std::filesystem::absolute(argv[1]);
        const auto dllHash=sha256(read(dllPath.c_str()));
        if(dllHash!="90e62a7f6d044b43e53413f1f0933647725838f60904de37cd4b49f3852a604c")
            throw std::runtime_error("DTS binary does not match the analyzed version");
        const auto data=read(argv[2]);
        const auto dataHash=sha256(data);
        SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
        // Only Windows' system directory is used for imported dependencies.
        module=LoadLibraryExW(dllPath.c_str(),nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!module) throw std::runtime_error("DTS LoadLibraryEx failed: "+std::to_string(GetLastError()));
        using Validator=std::uint64_t (*)(const void*);
        auto validate=reinterpret_cast<Validator>(reinterpret_cast<unsigned char*>(module)+(argc==4?0xc2c4:0xbec8));
        const auto status=static_cast<std::int32_t>(validate(data.data()));
        std::cout<<"{\"dll_sha256\":\""<<dllHash<<"\",\"spac_sha256\":\""<<dataHash
                 <<"\",\"native_status\":"<<status<<",\"accepted\":"<<(status==0?"true":"false")<<"}\n";
        FreeLibrary(module); module=nullptr;
        return status==0?0:3;
    } catch(const std::exception& error) {
        if(module) FreeLibrary(module);
        std::cerr<<error.what()<<'\n';
        return 2;
    }
}
