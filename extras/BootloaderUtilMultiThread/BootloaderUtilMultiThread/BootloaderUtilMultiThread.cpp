#include "BootloaderUtilMultiThread.h"


int CyBtldr_Program(const char* file, const uint8_t* securityKey,
    uint8_t appId, CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update)
{
    int result = CYRET_ABORT;

    CybtldrApi2* newProgram = new CybtldrApi2();
    if (newProgram)
    { 
        result = newProgram->CyBtldr_Program(file, securityKey, appId, comm, update);
        delete newProgram;
        newProgram = nullptr;
    }

    return result;
}