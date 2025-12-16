#include "CybtldrApi2.h"

#include <string.h>
#include <stdint.h>

CybtldrApi2::CybtldrApi2()
	: CybtldrParse()
	, g_abort(0)
{
}

int CybtldrApi2::ProcessDataRow_v0(CyBtldr_Action action, uint32_t rowSize, uint8_t* rowData, CyBtldr_ProgressUpdate* update)
{
    uint8_t buffer[MAX_BUFFER_SIZE];
    uint16_t bufSize;
    uint8_t arrayId;
    uint16_t rowNum;
    uint8_t checksum;

    int err;

    err = CyBtldr_ParseRowData(rowSize, rowData, &arrayId, &rowNum, buffer, &bufSize, &checksum);
    if (CYRET_SUCCESS == err)
    {
        switch (action)
        {
        case ERASE:
            err = CyBtldr_EraseRow(arrayId, rowNum);
            break;
        case PROGRAM:
            err = CyBtldr_ProgramRow(arrayId, rowNum, buffer, bufSize);
            if (CYRET_SUCCESS != err)
                break;
            /* Continue on to verify the row that was programmed */
        case VERIFY:
            checksum = (uint8_t)(checksum + arrayId + rowNum + (rowNum >> 8) + bufSize + (bufSize >> 8));
            err = CyBtldr_VerifyRow(arrayId, rowNum, checksum);
            break;
        }
    }
    if (CYRET_SUCCESS == err && NULL != update)
        update(arrayId, rowNum);

    return err;
}

int CybtldrApi2::ProcessDataRow_v1(CyBtldr_Action action, uint32_t rowSize, uint8_t* rowData, CyBtldr_ProgressUpdate* update)
{
    uint8_t buffer[MAX_BUFFER_SIZE];
    uint16_t bufSize;
    uint32_t address;
    uint8_t checksum;

    int err;

    err = CyBtldr_ParseRowData_v1(rowSize, rowData, &address, buffer, &bufSize, &checksum);

    if (CYRET_SUCCESS == err)
    {
        switch (action)
        {
        case ERASE:
            err = CyBtldr_EraseRow_v1(address);
            break;
        case PROGRAM:
            err = CyBtldr_ProgramRow_v1(address, buffer, bufSize);
            break;
        case VERIFY:
            err = CyBtldr_VerifyRow_v1(address, buffer, bufSize);
            break;
        }
    }
    if (CYRET_SUCCESS == err && NULL != update)
        update(0, (uint16_t)(address >> 16));

    return err;
}

int CybtldrApi2::ProcessMetaRow_v1(uint32_t rowSize, uint8_t* rowData)
{
    const uint32_t EIV_META_HEADER_SIZE = 5;
    const char EIV_META_HEADER[] = "@EIV:";

    uint8_t buffer[MAX_BUFFER_SIZE];
    uint16_t bufSize;


    int err = CYRET_SUCCESS;
    if (rowSize >= EIV_META_HEADER_SIZE && strncmp((char*)rowData, EIV_META_HEADER, EIV_META_HEADER_SIZE) == 0)
    {
        CyBtldr_FromAscii(rowSize - EIV_META_HEADER_SIZE, rowData + EIV_META_HEADER_SIZE, &bufSize, buffer);
        err = CyBtldr_SetEncryptionInitialVector(bufSize, buffer);
    }
    else
    {
        err = CYRET_ERR_FILE;
    }
    return err;
}

int CybtldrApi2::RunAction_v0(CyBtldr_Action action, uint32_t lineLen, uint8_t* line, uint8_t appId,
    const uint8_t* securityKey, CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update)
{
    const uint8_t INVALID_APP = 0xFF;
    uint32_t blVer = 0;
    uint32_t siliconId = 0;
    uint8_t siliconRev = 0;
    uint8_t chksumtype = SUM_CHECKSUM;
    uint8_t isValid;
    uint8_t isActive;
    int err;
    uint8_t bootloaderEntered = 0;

    err = CyBtldr_ParseHeader(lineLen, line, &siliconId, &siliconRev, &chksumtype);

    if (CYRET_SUCCESS == err)
    {
        CyBtldr_SetCheckSumType((CyBtldr_ChecksumType)chksumtype);

        err = CyBtldr_StartBootloadOperation(comm, siliconId, siliconRev, &blVer, securityKey);
        bootloaderEntered = 1;

        appId -= 1; /* 1 and 2 are legal inputs to function. 0 and 1 are valid for bootloader component */
        if (appId > 1)
        {
            appId = INVALID_APP;
        }

        if ((CYRET_SUCCESS == err) && (appId != INVALID_APP))
        {
            /* This will return error if bootloader is for single app */
            err = CyBtldr_GetApplicationStatus(appId, &isValid, &isActive);

            /* Active app can be verified, but not programmed or erased */
            if (CYRET_SUCCESS == err && VERIFY != action && isActive)
            {
                /* This is multi app */
                err = CYRET_ERR_ACTIVE;
            }
        }
    }

    while (CYRET_SUCCESS == err)
    {
        if (g_abort)
        {
            err = CYRET_ABORT;
            break;
        }

        err = CyBtldr_ReadLine(&lineLen, (char*)line);
        if (CYRET_SUCCESS == err)
        {
            err = ProcessDataRow_v0(action, lineLen, line, update);
        }
        else if (CYRET_ERR_EOF == err)
        {
            err = CYRET_SUCCESS;
            break;
        }
    }

    if (err == CYRET_SUCCESS)
    {
        if (PROGRAM == action && INVALID_APP != appId)
        {
            err = CyBtldr_GetApplicationStatus(appId, &isValid, &isActive);

            if (CYRET_SUCCESS == err)
            {
                /* If valid set the active application to what was just programmed */
                /* This is multi app */
                err = (0 == isValid)
                    ? CyBtldr_SetApplicationStatus(appId)
                    : CYRET_ERR_CHECKSUM;
            }
            else if (CYBTLDR_STAT_ERR_CMD == (err ^ (int)CYRET_ERR_BTLDR_MASK))
            {
                /* Single app - restore previous CYRET_SUCCESS */
                err = CYRET_SUCCESS;
            }
        }
        else if (PROGRAM == action || VERIFY == action)
        {
            err = CyBtldr_VerifyApplication();
        }
        CyBtldr_EndBootloadOperation();
    }
    else if (CYRET_ERR_COMM_MASK != (CYRET_ERR_COMM_MASK & err) && bootloaderEntered)
    {
        CyBtldr_EndBootloadOperation();
    }
    return err;
}

int CybtldrApi2::RunAction_v1(CyBtldr_Action action, uint32_t lineLen, uint8_t* line,
    CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update)
{
    uint32_t blVer = 0;
    uint32_t siliconId = 0;
    uint8_t siliconRev = 0;
    uint8_t chksumtype = SUM_CHECKSUM;
    uint8_t appId = 0;
    int err;
    uint8_t bootloaderEntered = 0;
    uint32_t applicationStartAddr = 0xffffffff;
    uint32_t applicationSize = 0;
    uint32_t productId = 0;

    err = CyBtldr_ParseHeader_v1(lineLen, line, &siliconId, &siliconRev, &chksumtype, &appId, &productId);

    if (CYRET_SUCCESS == err)
    {
        CyBtldr_SetCheckSumType((CyBtldr_ChecksumType)chksumtype);

        err = CyBtldr_StartBootloadOperation_v1(comm, siliconId, siliconRev, &blVer, productId);
        if (err == CYRET_SUCCESS)
            err = CyBtldr_ParseAppStartAndSize_v1(&applicationStartAddr, &applicationSize, line);
        if (err == CYRET_SUCCESS)
            err = CyBtldr_SetApplicationMetaData(appId, applicationStartAddr, applicationSize);
        bootloaderEntered = 1;
    }

    while (CYRET_SUCCESS == err)
    {
        if (g_abort)
        {
            err = CYRET_ABORT;
            break;
        }
        err = CyBtldr_ReadLine(&lineLen, (char*)line);
        if (CYRET_SUCCESS == err)
        {
            switch (line[0])
            {
            case '@':
                err = ProcessMetaRow_v1(lineLen, line);
                break;
            case ':':
                err = ProcessDataRow_v1(action, lineLen, line, update);
                break;
            }
        }
        else if (CYRET_ERR_EOF == err)
        {
            err = CYRET_SUCCESS;
            break;
        }
    }

    if (err == CYRET_SUCCESS && (PROGRAM == action || VERIFY == action))
    {
        err = CyBtldr_VerifyApplication_v1(appId);
        CyBtldr_EndBootloadOperation();
    }
    else if (CYRET_ERR_COMM_MASK != (CYRET_ERR_COMM_MASK & err) && bootloaderEntered)
    {
        CyBtldr_EndBootloadOperation();
    }
    return err;
}

int CybtldrApi2::CyBtldr_RunAction(CyBtldr_Action action, const char* file, const uint8_t* securityKey,
    uint8_t appId, CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update)
{
    g_abort = 0;
    uint32_t lineLen;
    uint8_t line[MAX_BUFFER_SIZE * 2]; // 2 hex characters per byte

    int err;
    uint8_t fileVersion = 0;

    err = CyBtldr_OpenDataFile(file);
    if (CYRET_SUCCESS == err)
    {
        err = CyBtldr_ReadLine(&lineLen, (char*)line);

        // The file version determine the format of the cyacd\cyacd2 file and the set of protocol commands used.
        if (CYRET_SUCCESS == err)
            err = CyBtldr_ParseCyacdFileVersion(file, lineLen, line, &fileVersion);
        if (CYRET_SUCCESS == err)
        {
            switch (fileVersion)
            {
            case 0:
                err = RunAction_v0(action, lineLen, line, appId, securityKey, comm, update);
                break;
            case 1:
                err = RunAction_v1(action, lineLen, line, comm, update);
                break;
            default:
                err = CYRET_ERR_FILE;
                break;

            }
        }

        CyBtldr_CloseDataFile();
    }

    return err;
}

int CybtldrApi2::CyBtldr_Program(const char* file, const uint8_t* securityKey, uint8_t appId,
    CyBtldr_CommunicationsData* comm, CyBtldr_ProgressUpdate* update)
{
    if (update != NULL)
        update(0x00, 0x0000);
    return CyBtldr_RunAction(PROGRAM, file, securityKey, appId, comm, update);
}

int CybtldrApi2::CyBtldr_Erase(const char* file, const uint8_t* securityKey, CyBtldr_CommunicationsData* comm,
    CyBtldr_ProgressUpdate* update)
{
    return CyBtldr_RunAction(ERASE, file, securityKey, 0, comm, update);
}

int CybtldrApi2::CyBtldr_Verify(const char* file, const uint8_t* securityKey, CyBtldr_CommunicationsData* comm,
    CyBtldr_ProgressUpdate* update)
{
    return CyBtldr_RunAction(VERIFY, file, securityKey, 0, comm, update);
}

int CybtldrApi2::CyBtldr_Abort(void)
{
    g_abort = 1;
    return CYRET_SUCCESS;
}
