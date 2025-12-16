#include "CybtldrCommand.h"

static void fillData16(uint8_t* buf, uint16_t data)
{
    buf[0] = (uint8_t)(data);
    buf[1] = (uint8_t)(data >> 8);
}

static void fillData32(uint8_t* buf, uint32_t data)
{
    fillData16(buf, (uint16_t)data);
    fillData16(buf + 2, (uint16_t)(data >> 16));
}

CybtldrCommand::CybtldrCommand()
	: CyBtldr_Checksum(SUM_CHECKSUM)
{
}

uint16_t CybtldrCommand::CyBtldr_ComputeChecksum16bit(uint8_t* buf, uint32_t size)
{
	if (CyBtldr_Checksum == CRC_CHECKSUM)
	{
		uint16_t crc = 0xffff;
		uint16_t tmp;
		int i;

		if (size == 0)
			return (~crc);

		do
		{
			for (i = 0, tmp = 0x00ff & *buf++; i < 8; i++, tmp >>= 1)
			{
				if ((crc & 0x0001) ^ (tmp & 0x0001))
					crc = (crc >> 1) ^ 0x8408;
				else
					crc >>= 1;
			}
		} while (--size);

		crc = ~crc;
		tmp = crc;
		crc = (crc << 8) | (tmp >> 8 & 0xFF);

		return crc;
	}
	else /* SUM_CHECKSUM */
	{
		uint16_t sum = 0;
		while (size-- > 0)
			sum += *buf++;

		return (1 + ~sum);
	}
}


uint32_t CybtldrCommand::CyBtldr_ComputeChecksum32bit(uint8_t* buf, uint32_t size)
{
    enum {
        g0 = 0x82F63B78,
        g1 = (g0 >> 1) & 0x7fffffff,
        g2 = (g0 >> 2) & 0x3fffffff,
        g3 = (g0 >> 3) & 0x1fffffff,
    };
    const static uint32_t table[16] =
    {
        0,                  (uint32_t)g3,           (uint32_t)g2,           (uint32_t)(g2 ^ g3),
        (uint32_t)g1,       (uint32_t)(g1 ^ g3),      (uint32_t)(g1 ^ g2),      (uint32_t)(g1 ^ g2 ^ g3),
        (uint32_t)g0,       (uint32_t)(g0 ^ g3),      (uint32_t)(g0 ^ g2),      (uint32_t)(g0 ^ g2 ^ g3),
        (uint32_t)(g0 ^ g1),  (uint32_t)(g0 ^ g1 ^ g3),   (uint32_t)(g0 ^ g1 ^ g2),   (uint32_t)(g0 ^ g1 ^ g2 ^ g3),
    };

    uint8_t* data = (uint8_t*)buf;
    uint32_t crc = 0xFFFFFFFF;
    while (size != 0)
    {
        int i;
        --size;
        crc = crc ^ (*data);
        ++data;
        for (i = 1; i >= 0; i--)
        {
            crc = (crc >> 4) ^ table[crc & 0xF];
        }
    }
    return ~crc;
}

void CybtldrCommand::CyBtldr_SetCheckSumType(CyBtldr_ChecksumType chksumType)
{
    CyBtldr_Checksum = chksumType;
}

static int ParseGenericCmdResult(uint8_t* cmdBuf, uint32_t dataSize, uint32_t expectedSize, uint8_t* status)
{
    int err = CYRET_SUCCESS;
    uint32_t cmdSize = dataSize + BASE_CMD_SIZE;
    *status = cmdBuf[1];
    if (cmdSize != expectedSize)
        err = CYRET_ERR_LENGTH;
    else if (*status != CYRET_SUCCESS)
        err = CYRET_ERR_BTLDR_MASK | (*status);
    else if (cmdBuf[0] != CMD_START || cmdBuf[2] != ((uint8_t)dataSize) || cmdBuf[3] != ((uint8_t)(dataSize >> 8)) || cmdBuf[cmdSize - 1] != CMD_STOP)
        err = CYRET_ERR_DATA;
    return err;
}

int CybtldrCommand::CyBtldr_ParseDefaultCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status)
{
    return ParseGenericCmdResult(cmdBuf, 0, cmdSize, status);
}

// NOTE: If the cmd contains data bytes, make sure to call this after setting data bytes.
// Otherwise the checksum here will not include the data bytes.
int CybtldrCommand::CreateCmd(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t cmdCode)
{
    uint16_t checksum;
    cmdBuf[0] = CMD_START;
    cmdBuf[1] = cmdCode;
    fillData16(cmdBuf + 2, (uint16_t)cmdSize - BASE_CMD_SIZE);
    checksum = CyBtldr_ComputeChecksum16bit(cmdBuf, cmdSize - 3);
    fillData16(cmdBuf + cmdSize - 3, checksum);
    cmdBuf[cmdSize - 1] = CMD_STOP;
    return CYRET_SUCCESS;
}

int CybtldrCommand::CyBtldr_CreateEnterBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize, const uint8_t* securityKeyBuf)
{
    const uint16_t RESULT_DATA_SIZE = 8;
    const uint16_t BOOTLOADER_SECURITY_KEY_SIZE = 6;
    uint16_t commandDataSize;
    uint16_t i;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    if (securityKeyBuf != NULL)
        commandDataSize = BOOTLOADER_SECURITY_KEY_SIZE;
    else
        commandDataSize = 0;
    *cmdSize = BASE_CMD_SIZE + commandDataSize;

    for (i = 0; i < commandDataSize; i++)
        cmdBuf[i + 4] = securityKeyBuf[i];
    return CreateCmd(cmdBuf, *cmdSize, CMD_ENTER_BOOTLOADER);
}

int CybtldrCommand::CyBtldr_CreateEnterBootLoaderCmd_v1(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize, uint32_t productID)
{
    const uint16_t COMMAND_DATA_SIZE = 6;
    const uint16_t RESULT_DATA_SIZE = 8;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    fillData32(cmdBuf + 4, productID);
    fillData16(cmdBuf + 8, 0);
    return CreateCmd(cmdBuf, *cmdSize, CMD_ENTER_BOOTLOADER);
}

int CybtldrCommand::CyBtldr_ParseEnterBootLoaderCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint32_t* siliconId, uint8_t* siliconRev, uint32_t* blVersion, uint8_t* status)
{
    const uint32_t RESULT_DATA_SIZE = 8;
    int err = ParseGenericCmdResult(cmdBuf, RESULT_DATA_SIZE, cmdSize, status);

    if (CYRET_SUCCESS == err)
    {
        *siliconId = (cmdBuf[7] << 24) | (cmdBuf[6] << 16) | (cmdBuf[5] << 8) | cmdBuf[4];
        *siliconRev = cmdBuf[8];
        *blVersion = (cmdBuf[11] << 16) | (cmdBuf[10] << 8) | cmdBuf[9];
    }
    return err;
}

int CybtldrCommand::CyBtldr_CreateExitBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    *cmdSize = BASE_CMD_SIZE;
    *resSize = BASE_CMD_SIZE;
    return CreateCmd(cmdBuf, *cmdSize, CMD_EXIT_BOOTLOADER);
}

int CybtldrCommand::CyBtldr_CreateProgramRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 3;
    uint32_t i;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE + size;

    cmdBuf[4] = arrayId;
    fillData16(cmdBuf + 5, rowNum);
    for (i = 0; i < size; i++)
        cmdBuf[i + 7] = buf[i];
    return CreateCmd(cmdBuf, *cmdSize, CMD_PROGRAM_ROW);
}

int CybtldrCommand::CyBtldr_ParseProgramRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status)
{
    return CyBtldr_ParseDefaultCmdResult(cmdBuf, cmdSize, status);
}

int CybtldrCommand::CyBtldr_CreateVerifyRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t RESULT_DATA_SIZE = 1;
    const uint16_t COMMAND_DATA_SIZE = 3;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = arrayId;
    fillData16(cmdBuf + 5, rowNum);
    return CreateCmd(cmdBuf, *cmdSize, CMD_GET_ROW_CHECKSUM);
}

int CybtldrCommand::CyBtldr_ParseVerifyRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* checksum, uint8_t* status)
{
    const uint16_t RESULT_DATA_SIZE = 1;
    int err = ParseGenericCmdResult(cmdBuf, RESULT_DATA_SIZE, cmdSize, status);
    if (CYRET_SUCCESS == err)
    {
        *checksum = cmdBuf[4];
    }
    return err;
}

int CybtldrCommand::CyBtldr_CreateEraseRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 3;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = arrayId;
    fillData16(cmdBuf + 5, rowNum);
    return CreateCmd(cmdBuf, *cmdSize, CMD_ERASE_ROW);
}

int CybtldrCommand::CyBtldr_ParseEraseRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status)
{
    return CyBtldr_ParseDefaultCmdResult(cmdBuf, cmdSize, status);
}

int CybtldrCommand::CyBtldr_CreateVerifyChecksumCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t RESULT_DATA_SIZE = 1;
    *cmdSize = BASE_CMD_SIZE;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    return CreateCmd(cmdBuf, *cmdSize, CMD_VERIFY_CHECKSUM);
}

int CybtldrCommand::CyBtldr_ParseVerifyChecksumCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* checksumValid, uint8_t* status)
{
    const uint16_t RESULT_DATA_SIZE = 1;
    int err = ParseGenericCmdResult(cmdBuf, RESULT_DATA_SIZE, cmdSize, status);
    if (CYRET_SUCCESS == err)
    {
        *checksumValid = cmdBuf[4];
    }
    return err;
}

int CybtldrCommand::CyBtldr_CreateGetFlashSizeCmd(uint8_t arrayId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t RESULT_DATA_SIZE = 4;
    const uint16_t COMMAND_DATA_SIZE = 1;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = arrayId;
    return CreateCmd(cmdBuf, *cmdSize, CMD_GET_FLASH_SIZE);
}

int CybtldrCommand::CyBtldr_ParseGetFlashSizeCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint16_t* startRow, uint16_t* endRow, uint8_t* status)
{
    const uint32_t RESULT_DATA_SIZE = 4;
    int err = ParseGenericCmdResult(cmdBuf, RESULT_DATA_SIZE, cmdSize, status);
    if (CYRET_SUCCESS == err)
    {
        *startRow = (cmdBuf[5] << 8) | cmdBuf[4];
        *endRow = (cmdBuf[7] << 8) | cmdBuf[6];
    }
    return err;
}

int CybtldrCommand::CyBtldr_CreateSendDataCmd(uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    uint16_t i;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = size + BASE_CMD_SIZE;

    for (i = 0; i < size; i++)
        cmdBuf[i + 4] = buf[i];
    return CreateCmd(cmdBuf, *cmdSize, CMD_SEND_DATA);
}

int CybtldrCommand::CyBtldr_ParseSendDataCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status)
{
    return CyBtldr_ParseDefaultCmdResult(cmdBuf, cmdSize, status);
}

int CybtldrCommand::CyBtldr_CreateSyncBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    *cmdSize = BASE_CMD_SIZE;
    *resSize = BASE_CMD_SIZE;

    return CreateCmd(cmdBuf, *cmdSize, CMD_SYNC);
}

int CybtldrCommand::CyBtldr_CreateGetAppStatusCmd(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t RESULT_DATA_SIZE = 2;
    const uint16_t COMMAND_DATA_SIZE = 1;
    *resSize = BASE_CMD_SIZE + RESULT_DATA_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = appId;
    return CreateCmd(cmdBuf, *cmdSize, CMD_GET_APP_STATUS);
}

int CybtldrCommand::CyBtldr_ParseGetAppStatusCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* isValid, uint8_t* isActive, uint8_t* status)
{
    const uint16_t RESULT_DATA_SIZE = 2;
    int err = ParseGenericCmdResult(cmdBuf, RESULT_DATA_SIZE, cmdSize, status);
    if (CYRET_SUCCESS == err)
    {
        *isValid = cmdBuf[4];
        *isActive = cmdBuf[5];
    }
    return err;
}

int CybtldrCommand::CyBtldr_CreateSetActiveAppCmd(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 1;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = appId;
    return CreateCmd(cmdBuf, *cmdSize, CMD_SET_ACTIVE_APP);
}

int CybtldrCommand::CyBtldr_ParseSetActiveAppCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status)
{
    return CyBtldr_ParseDefaultCmdResult(cmdBuf, cmdSize, status);
}

int CybtldrCommand::CyBtldr_CreateProgramDataCmd(uint32_t address, uint32_t chksum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 8;
    uint16_t i;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE + size;

    fillData32(cmdBuf + 4, address);
    fillData32(cmdBuf + 8, chksum);
    for (i = 0; i < size; i++)
        cmdBuf[i + 4 + COMMAND_DATA_SIZE] = buf[i];
    return  CreateCmd(cmdBuf, *cmdSize, CMD_PROGRAM_DATA);
}

int CybtldrCommand::CyBtldr_CreateVerifyDataCmd(uint32_t address, uint32_t chksum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 8;
    uint16_t i;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE + size;

    fillData32(cmdBuf + 4, address);
    fillData32(cmdBuf + 8, chksum);
    for (i = 0; i < size; i++)
        cmdBuf[i + 4 + COMMAND_DATA_SIZE] = buf[i];
    return CreateCmd(cmdBuf, *cmdSize, CMD_VERIFY_DATA);
}

int CybtldrCommand::CyBtldr_CreateEraseDataCmd(uint32_t address, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 4;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    fillData32(cmdBuf + 4, address);
    return CreateCmd(cmdBuf, *cmdSize, CMD_ERASE_DATA);
}

int CybtldrCommand::CyBtldr_CreateVerifyChecksumCmd_v1(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    const uint16_t COMMAND_DATA_SIZE = 1;
    *resSize = BASE_CMD_SIZE + 1;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = appId;
    return CreateCmd(cmdBuf, *cmdSize, CMD_VERIFY_CHECKSUM);
}

int CybtldrCommand::CyBtldr_CreateSetApplicationMetadataCmd(uint8_t appID, uint8_t* buf, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    uint32_t i;
    const uint16_t BTDLR_SDK_METADATA_SIZE = 8;
    const uint16_t COMMAND_DATA_SIZE = BTDLR_SDK_METADATA_SIZE + 1;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + COMMAND_DATA_SIZE;

    cmdBuf[4] = appID;
    for (i = 0; i < BTDLR_SDK_METADATA_SIZE; i++)
    {
        cmdBuf[5 + i] = buf[i];
    }
    return CreateCmd(cmdBuf, *cmdSize, CMD_SET_METADATA);
}

int CybtldrCommand::CyBtldr_CreateSetEncryptionInitialVectorCmd(uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize)
{
    uint32_t i;
    *resSize = BASE_CMD_SIZE;
    *cmdSize = BASE_CMD_SIZE + size;
    for (i = 0; i < size; i++)
    {
        cmdBuf[4 + i] = buf[i];
    }
    return CreateCmd(cmdBuf, *cmdSize, CMD_SET_EIV);
}

//Try to parse a packet to determine its validity, if valid then return set the status param to the packet's status.
//Used to generate useful error messages. return 1 on success 0 otherwise.
int CybtldrCommand::CyBtldr_TryParseParketStatus(uint8_t* packet, int packetSize, uint8_t* status)
{
    uint16_t dataSize;
    uint16_t readChecksum;
    uint16_t computedChecksum;
    if (packet == NULL || packetSize < BASE_CMD_SIZE || packet[0] != CMD_START)
        return CYBTLDR_STAT_ERR_UNK;
    *status = packet[1];
    dataSize = packet[2] | (packet[3] << 8);

    readChecksum = packet[dataSize + 4] | (packet[dataSize + 5] << 8);
    computedChecksum = CyBtldr_ComputeChecksum16bit(packet, BASE_CMD_SIZE + dataSize - 3);

    if (packet[dataSize + BASE_CMD_SIZE - 1] != CMD_STOP || readChecksum != computedChecksum)
        return CYBTLDR_STAT_ERR_UNK;
    return CYRET_SUCCESS;
}