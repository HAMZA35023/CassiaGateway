#pragma once
#define EXTERN extern "C"
#define CALL_CON
#include "CybtldrApi2.h"

EXTERN int CALL_CON CyBtldr_Program(const char* file, const uint8_t* securityKey,
    uint8_t appId, CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update);