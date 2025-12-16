#include "CybtldrApi1.h"

#include <algorithm>


CybtldrApi1::CybtldrApi1()
	: CybtldrCommand()
	, g_comm(nullptr)
{
}

int CybtldrApi1::CyBtldr_TransferData(uint8_t* inBuf, int inSize, uint8_t* outBuf, int outSize)
{
    int err = g_comm->WriteData(inBuf, inSize);

    if (CYRET_SUCCESS == err)
        err = g_comm->ReadData(outBuf, outSize);

    if (CYRET_SUCCESS != err)
        err |= CYRET_ERR_COMM_MASK;

    return err;
}

int CybtldrApi1::CyBtldr_ValidateRow(uint8_t arrayId, uint16_t rowNum)
{
    uint32_t inSize;
    uint32_t outSize;
    uint16_t minRow = 0;
    uint16_t maxRow = 0;
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    if (arrayId < MAX_FLASH_ARRAYS)
    {
        if (NO_FLASH_ARRAY_DATA == g_validRows[arrayId])
        {
            err = CyBtldr_CreateGetFlashSizeCmd(arrayId, inBuf, &inSize, &outSize);
            if (CYRET_SUCCESS == err)
                err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
            if (CYRET_SUCCESS == err)
                err = CyBtldr_ParseGetFlashSizeCmdResult(outBuf, outSize, &minRow, &maxRow, &status);
            if (CYRET_SUCCESS != status)
                err = status | CYRET_ERR_BTLDR_MASK;

            if (CYRET_SUCCESS == err)
            {
                if (CYRET_SUCCESS == status)
                    g_validRows[arrayId] = (minRow << 16) + maxRow;
                else
                    err = status | CYRET_ERR_BTLDR_MASK;
            }
        }
        if (CYRET_SUCCESS == err)
        {
            minRow = (uint16_t)(g_validRows[arrayId] >> 16);
            maxRow = (uint16_t)g_validRows[arrayId];
            if (rowNum < minRow || rowNum > maxRow)
                err = CYRET_ERR_ROW;
        }
    }
    else
        err = CYRET_ERR_ARRAY;

    return err;
}

int CybtldrApi1::CyBtldr_StartBootloadOperation(CyBtldr_CommunicationsData* comm, uint32_t expSiId,
    uint8_t expSiRev, uint32_t* blVer, const uint8_t* securityKeyBuf)
{
    const uint32_t SUPPORTED_BOOTLOADER = 0x010000;
    const uint32_t BOOTLOADER_VERSION_MASK = 0xFF0000;
    uint32_t i;
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint32_t siliconId = 0;
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint8_t siliconRev = 0;
    uint8_t status = CYRET_SUCCESS;
    int err;

    g_comm = comm;
    for (i = 0; i < MAX_FLASH_ARRAYS; i++)
        g_validRows[i] = NO_FLASH_ARRAY_DATA;

    err = g_comm->OpenConnection();
    if (CYRET_SUCCESS != err)
        err |= CYRET_ERR_COMM_MASK;

    if (CYRET_SUCCESS == err)
        err = CyBtldr_CreateEnterBootLoaderCmd(inBuf, &inSize, &outSize, securityKeyBuf);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseEnterBootLoaderCmdResult(outBuf, outSize, &siliconId, &siliconRev, blVer, &status);
    else if (CyBtldr_TryParseParketStatus(outBuf, outSize, &status) == CYRET_SUCCESS)
        err = status | CYRET_ERR_BTLDR_MASK; //if the response we get back is a valid packet override the err with the response's status

    if (CYRET_SUCCESS == err)
    {
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
        if (expSiId != siliconId || expSiRev != siliconRev)
            err = CYRET_ERR_DEVICE;
        else if ((*blVer & BOOTLOADER_VERSION_MASK) != SUPPORTED_BOOTLOADER)
            err = CYRET_ERR_VERSION;
    }

    return err;
}

int CybtldrApi1::CyBtldr_StartBootloadOperation_v1(CyBtldr_CommunicationsData* comm, uint32_t expSiId,
    uint8_t expSiRev, uint32_t* blVer, uint32_t productID)
{
    uint32_t i;
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint32_t siliconId = 0;
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint8_t siliconRev = 0;
    uint8_t status = CYRET_SUCCESS;
    int err;

    g_comm = comm;
    for (i = 0; i < MAX_FLASH_ARRAYS; i++)
        g_validRows[i] = NO_FLASH_ARRAY_DATA;

    err = g_comm->OpenConnection();
    if (CYRET_SUCCESS != err)
        err |= CYRET_ERR_COMM_MASK;

    if (CYRET_SUCCESS == err)
        err = CyBtldr_CreateEnterBootLoaderCmd_v1(inBuf, &inSize, &outSize, productID);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseEnterBootLoaderCmdResult(outBuf, outSize, &siliconId, &siliconRev, blVer, &status);
    else if (CyBtldr_TryParseParketStatus(outBuf, outSize, &status) == CYRET_SUCCESS)
        err = status | CYRET_ERR_BTLDR_MASK; //if the response we get back is a valid packet override the err with the response's status

    if (CYRET_SUCCESS == err)
    {
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
        if (expSiId != siliconId || expSiRev != siliconRev)
            err = CYRET_ERR_DEVICE;
    }

    return err;
}

int CybtldrApi1::CyBtldr_GetApplicationStatus(uint8_t appID, uint8_t* isValid, uint8_t* isActive)
{
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint8_t status = CYRET_SUCCESS;
    int err;

    err = CyBtldr_CreateGetAppStatusCmd(appID, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseGetAppStatusCmdResult(outBuf, outSize, isValid, isActive, &status);
    else if (CyBtldr_TryParseParketStatus(outBuf, outSize, &status) == CYRET_SUCCESS)
        err = status | CYRET_ERR_BTLDR_MASK; //if the response we get back is a valid packet override the err with the response's status

    if (CYRET_SUCCESS == err)
    {
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
    }

    return err;
}

int CybtldrApi1::CyBtldr_SetApplicationStatus(uint8_t appID)
{
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint8_t status = CYRET_SUCCESS;
    int err;

    err = CyBtldr_CreateSetActiveAppCmd(appID, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseSetActiveAppCmdResult(outBuf, outSize, &status);

    if (CYRET_SUCCESS == err)
    {
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
    }

    return err;
}

int CybtldrApi1::CyBtldr_EndBootloadOperation(void)
{
    uint32_t inSize;
    uint32_t outSize;
    uint8_t inBuf[MAX_COMMAND_SIZE];

    int err = CyBtldr_CreateExitBootLoaderCmd(inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
    {
        err = g_comm->WriteData(inBuf, inSize);

        if (CYRET_SUCCESS == err)
            err = g_comm->CloseConnection();

        if (CYRET_SUCCESS != err)
            err |= CYRET_ERR_COMM_MASK;
    }
    g_comm = NULL;

    return err;
}

int CybtldrApi1::SendData(uint8_t* buf, uint16_t size, uint16_t* offset, uint16_t maxRemainingDataSize, uint8_t* inBuf, uint8_t* outBuf)
{
    uint8_t status = CYRET_SUCCESS;
    uint32_t inSize = 0, outSize = 0;
    // size is the total bytes of data to transfer.
    // offset is the amount of data already transfered.
    // a is maximum amount of data allowed to be left over when this function ends.
    // (we can leave some data for caller (programRow, VerifyRow,...) to send.
    // TRANSFER_HEADER_SIZE is the amount of bytes this command header takes up.
    const uint16_t TRANSFER_HEADER_SIZE = 7;
    uint16_t subBufSize = std::min((uint16_t)(g_comm->MaxTransferSize - TRANSFER_HEADER_SIZE), size);
    int err = CYRET_SUCCESS;
    //Break row into pieces to ensure we don't send too much for the transfer protocol
    while ((CYRET_SUCCESS == err) && ((size - (*offset)) > maxRemainingDataSize))
    {
        err = CyBtldr_CreateSendDataCmd(&buf[*offset], subBufSize, inBuf, &inSize, &outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_ParseSendDataCmdResult(outBuf, outSize, &status);
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;

        (*offset) += subBufSize;
    }
    return err;
}

int CybtldrApi1::CyBtldr_ProgramRow(uint8_t arrayID, uint16_t rowNum, uint8_t* buf, uint16_t size)
{
    const int TRANSFER_HEADER_SIZE = 10;

    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize;
    uint32_t outSize;
    uint16_t offset = 0;
    uint16_t subBufSize;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    if (arrayID < MAX_FLASH_ARRAYS)
        err = CyBtldr_ValidateRow(arrayID, rowNum);

    if (CYRET_SUCCESS == err)
        err = SendData(buf, size, &offset, (uint16_t)(g_comm->MaxTransferSize - TRANSFER_HEADER_SIZE), inBuf, outBuf);

    if (CYRET_SUCCESS == err)
    {
        subBufSize = (uint16_t)(size - offset);

        err = CyBtldr_CreateProgramRowCmd(arrayID, rowNum, &buf[offset], subBufSize, inBuf, &inSize, &outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_ParseProgramRowCmdResult(outBuf, outSize, &status);
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
    }

    return err;
}

int CybtldrApi1::CyBtldr_EraseRow(uint8_t arrayID, uint16_t rowNum)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    if (arrayID < MAX_FLASH_ARRAYS)
        err = CyBtldr_ValidateRow(arrayID, rowNum);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_CreateEraseRowCmd(arrayID, rowNum, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseEraseRowCmdResult(outBuf, outSize, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;

    return err;
}

int CybtldrApi1::CyBtldr_VerifyRow(uint8_t arrayID, uint16_t rowNum, uint8_t checksum)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t rowChecksum = 0;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    if (arrayID < MAX_FLASH_ARRAYS)
        err = CyBtldr_ValidateRow(arrayID, rowNum);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_CreateVerifyRowCmd(arrayID, rowNum, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseVerifyRowCmdResult(outBuf, outSize, &rowChecksum, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;
    if ((CYRET_SUCCESS == err) && (rowChecksum != checksum))
        err = CYRET_ERR_CHECKSUM;

    return err;
}

int CybtldrApi1::CyBtldr_VerifyApplication()
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t checksumValid = 0;
    uint8_t status = CYRET_SUCCESS;

    int err = CyBtldr_CreateVerifyChecksumCmd(inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseVerifyChecksumCmdResult(outBuf, outSize, &checksumValid, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;
    if ((CYRET_SUCCESS == err) && (!checksumValid))
        err = CYRET_ERR_CHECKSUM;

    return err;
}

int CybtldrApi1::CyBtldr_ProgramRow_v1(uint32_t address, uint8_t* buf, uint16_t size)
{
    const int TRANSFER_HEADER_SIZE = 15;

    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize;
    uint32_t outSize;
    uint16_t offset = 0;
    uint16_t subBufSize;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    uint32_t chksum = CyBtldr_ComputeChecksum32bit(buf, size);

    if (CYRET_SUCCESS == err)
        err = SendData(buf, size, &offset, (uint16_t)(g_comm->MaxTransferSize - TRANSFER_HEADER_SIZE), inBuf, outBuf);

    if (CYRET_SUCCESS == err)
    {
        subBufSize = (uint16_t)(size - offset);

        err = CyBtldr_CreateProgramDataCmd(address, chksum, &buf[offset], subBufSize, inBuf, &inSize, &outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_ParseDefaultCmdResult(outBuf, outSize, &status);
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
    }

    return err;
}

int CybtldrApi1::CyBtldr_EraseRow_v1(uint32_t address)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    if (CYRET_SUCCESS == err)
        err = CyBtldr_CreateEraseDataCmd(address, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseEraseRowCmdResult(outBuf, outSize, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;

    return err;
}

int CybtldrApi1::CyBtldr_VerifyRow_v1(uint32_t address, uint8_t* buf, uint16_t size)
{
    const int TRANSFER_HEADER_SIZE = 15;

    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize;
    uint32_t outSize;
    uint16_t offset = 0;
    uint16_t subBufSize;
    uint8_t status = CYRET_SUCCESS;
    int err = CYRET_SUCCESS;

    uint32_t chksum = CyBtldr_ComputeChecksum32bit(buf, size);

    if (CYRET_SUCCESS == err)
        err = SendData(buf, size, &offset, (uint16_t)(g_comm->MaxTransferSize - TRANSFER_HEADER_SIZE), inBuf, outBuf);

    if (CYRET_SUCCESS == err)
    {
        subBufSize = (uint16_t)(size - offset);

        err = CyBtldr_CreateVerifyDataCmd(address, chksum, &buf[offset], subBufSize, inBuf, &inSize, &outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
        if (CYRET_SUCCESS == err)
            err = CyBtldr_ParseDefaultCmdResult(outBuf, outSize, &status);
        if (CYRET_SUCCESS != status)
            err = status | CYRET_ERR_BTLDR_MASK;
    }

    return err;
}

int CybtldrApi1::CyBtldr_VerifyApplication_v1(uint8_t appId)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t checksumValid = 0;
    uint8_t status = CYRET_SUCCESS;

    int err = CyBtldr_CreateVerifyChecksumCmd_v1(appId, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseVerifyChecksumCmdResult(outBuf, outSize, &checksumValid, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;
    if ((CYRET_SUCCESS == err) && (!checksumValid))
        err = CYRET_ERR_CHECKSUM;

    return err;
}

int CybtldrApi1::CyBtldr_SetApplicationMetaData(uint8_t appId, uint32_t appStartAddr, uint32_t appSize)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];
    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t status = CYRET_SUCCESS;

    uint8_t metadata[8];
    metadata[0] = (uint8_t)appStartAddr;
    metadata[1] = (uint8_t)(appStartAddr >> 8);
    metadata[2] = (uint8_t)(appStartAddr >> 16);
    metadata[3] = (uint8_t)(appStartAddr >> 24);
    metadata[4] = (uint8_t)appSize;
    metadata[5] = (uint8_t)(appSize >> 8);
    metadata[6] = (uint8_t)(appSize >> 16);
    metadata[7] = (uint8_t)(appSize >> 24);
    int err = CyBtldr_CreateSetApplicationMetadataCmd(appId, metadata, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseDefaultCmdResult(outBuf, outSize, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;

    return err;
}

int CybtldrApi1::CyBtldr_SetEncryptionInitialVector(uint16_t size, uint8_t* buf)
{
    uint8_t inBuf[MAX_COMMAND_SIZE];
    uint8_t outBuf[MAX_COMMAND_SIZE];

    uint32_t inSize = 0;
    uint32_t outSize = 0;
    uint8_t status = CYRET_SUCCESS;

    int err = CyBtldr_CreateSetEncryptionInitialVectorCmd(buf, size, inBuf, &inSize, &outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_TransferData(inBuf, inSize, outBuf, outSize);
    if (CYRET_SUCCESS == err)
        err = CyBtldr_ParseDefaultCmdResult(outBuf, outSize, &status);
    if (CYRET_SUCCESS != status)
        err = status | CYRET_ERR_BTLDR_MASK;

    return err;
}