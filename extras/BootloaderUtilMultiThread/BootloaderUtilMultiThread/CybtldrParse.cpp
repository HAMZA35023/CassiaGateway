#include "CybtldrParse.h"

#include <string.h>
#include <ctype.h>

static uint16_t parse2ByteValueBigEndian(uint8_t* buf)
{
    return ((uint16_t)(buf[0] << 8)) | ((uint16_t)buf[1]);
}

static uint32_t parse4ByteValueBigEndian(uint8_t* buf)
{
    return (((uint32_t)parse2ByteValueBigEndian(buf)) << 16) | ((uint32_t)parse2ByteValueBigEndian(buf + 2));
}

static uint16_t parse2ByteValueLittleEndian(uint8_t* buf)
{
    return ((uint16_t)buf[0]) | (((uint16_t)buf[1]) << 8);
}

static uint32_t parse4ByteValueLittleEndian(uint8_t* buf)
{
    return ((uint32_t)parse2ByteValueLittleEndian(buf)) | (((uint32_t)parse2ByteValueLittleEndian(buf + 2)) << 16);
}

static int stringEqualIgnoreCase(const char* str1, const char* str2)
{
    while (((*str1) != 0 && (*str2) != 0))
    {
        if (tolower(*str1) != tolower(*str2))
        {
            return 0;
        }
        str1++;
        str2++;
    }
    return (tolower(*str1) == tolower(*str2));
}


CybtldrParse::CybtldrParse()
	: CybtldrApi1()
	, dataFile(nullptr)
{
}


uint8_t CybtldrParse::CyBtldr_FromHex(char value)
{
    if ('0' <= value && value <= '9')
        return (uint8_t)(value - '0');
    if ('a' <= value && value <= 'f')
        return (uint8_t)(10 + value - 'a');
    if ('A' <= value && value <= 'F')
        return (uint8_t)(10 + value - 'A');
    return 0;
}

int CybtldrParse::CyBtldr_FromAscii(uint32_t bufSize, uint8_t* buffer, uint16_t* rowSize, uint8_t* rowData)
{
    uint16_t i;
    int err = CYRET_SUCCESS;

    if (bufSize & 1) // Make sure even number of bytes
        err = CYRET_ERR_LENGTH;
    else
    {
        for (i = 0; i < bufSize / 2; i++)
        {
            rowData[i] = (CyBtldr_FromHex(buffer[i * 2]) << 4) | CyBtldr_FromHex(buffer[i * 2 + 1]);
        }
        *rowSize = i;
    }

    return err;
}

int CybtldrParse::CyBtldr_ReadLine(uint32_t* size, char* buffer)
{
    int err = CYRET_SUCCESS;
    uint32_t len;
    // line that start with '#' are assumed to be comments, continue reading if we read a comment
    do
    {
        len = 0;
        if (NULL != dataFile && !feof(dataFile))
        {
            if (NULL != fgets(buffer, MAX_BUFFER_SIZE * 2, dataFile))
            {
                len = strlen(buffer);

                while (len > 0 && ('\n' == buffer[len - 1] || '\r' == buffer[len - 1]))
                    --len;
            }
            else
                err = CYRET_ERR_EOF;
        }
        else
            err = CYRET_ERR_FILE;
    } while (err == CYRET_SUCCESS && buffer[0] == '#');

    *size = len;
    return err;
}

int CybtldrParse::CyBtldr_OpenDataFile(const char* file)
{
    dataFile = fopen(file, "r");

    return (NULL == dataFile)
        ? CYRET_ERR_FILE
        : CYRET_SUCCESS;
}

int CybtldrParse::CyBtldr_ParseCyacdFileVersion(const char* fileName, uint32_t bufSize, uint8_t* header, uint8_t* version)
{
    // check file extension of the file, if extension is cyacd, version 0
    int index = strlen(fileName);
    int err = CYRET_SUCCESS;
    if (bufSize == 0)
    {
        err = CYRET_ERR_FILE;
    }
    while (CYRET_SUCCESS == err && fileName[--index] != '.')
    {
        if (index == 0)
            err = CYRET_ERR_FILE;
    }
    if (stringEqualIgnoreCase(fileName + index, ".cyacd2"))
    {
        if (bufSize < 2)
            err = CYRET_ERR_FILE;
        // .cyacd2 file stores version information in the first byte of the file header.
        if (CYRET_SUCCESS == err)
        {
            *version = CyBtldr_FromHex(header[0]) << 4 | CyBtldr_FromHex(header[1]);
            if (*version == 0)
                err = CYRET_ERR_DATA;
        }
    }
    else if (stringEqualIgnoreCase(fileName + index, ".cyacd"))
    {
        // .cyacd file does not contain version information
        *version = 0;
    }
    else
    {
        err = CYRET_ERR_FILE;
    }
    return err;
}

int CybtldrParse::CyBtldr_ParseHeader(uint32_t bufSize, uint8_t* buffer, uint32_t* siliconId, uint8_t* siliconRev, uint8_t* chksum)
{
    int err = CYRET_SUCCESS;

    uint16_t rowSize = 0;
    uint8_t rowData[MAX_BUFFER_SIZE];
    if (CYRET_SUCCESS == err)
    {
        err = CyBtldr_FromAscii(bufSize, buffer, &rowSize, rowData);
    }
    if (CYRET_SUCCESS == err)
    {
        if (rowSize > 5)
            *chksum = rowData[5];
        else
            *chksum = 0;
        if (rowSize > 4)
        {
            *siliconId = parse4ByteValueBigEndian(rowData);
            *siliconRev = rowData[4];
        }
        else
            err = CYRET_ERR_LENGTH;
    }
    return err;
}

int CybtldrParse::CyBtldr_ParseHeader_v1(uint32_t bufSize, uint8_t* buffer, uint32_t* siliconId,
    uint8_t* siliconRev, uint8_t* chksum, uint8_t* appID, uint32_t* productID)
{
    int err = CYRET_SUCCESS;

    uint16_t rowSize = 0;
    uint8_t rowData[MAX_BUFFER_SIZE];
    if (CYRET_SUCCESS == err)
    {
        err = CyBtldr_FromAscii(bufSize, buffer, &rowSize, rowData);
    }
    if (CYRET_SUCCESS == err)
    {
        if (rowSize == 12)
        {
            *siliconId = parse4ByteValueLittleEndian(rowData + 1);
            *siliconRev = rowData[5];
            *chksum = rowData[6];
            *appID = rowData[7];
            *productID = parse4ByteValueLittleEndian(rowData + 8);
        }
        else
        {
            err = CYRET_ERR_LENGTH;
        }
    }
    return err;
}

int CybtldrParse::CyBtldr_ParseRowData(uint32_t bufSize, uint8_t* buffer, uint8_t* arrayId, uint16_t* rowNum, uint8_t* rowData, uint16_t* size, uint8_t* checksum)
{
    const uint16_t MIN_SIZE = 6; //1-array, 2-addr, 2-size, 1-checksum
    const int DATA_OFFSET = 5;

    unsigned int i;
    uint16_t hexSize;
    uint8_t hexData[MAX_BUFFER_SIZE];
    int err = CYRET_SUCCESS;

    if (bufSize <= MIN_SIZE)
        err = CYRET_ERR_LENGTH;
    else if (buffer[0] == ':')
    {
        err = CyBtldr_FromAscii(bufSize - 1, &buffer[1], &hexSize, hexData);

        if (err == CYRET_SUCCESS)
        {
            *arrayId = hexData[0];
            *rowNum = parse2ByteValueBigEndian(hexData + 1);
            *size = parse2ByteValueBigEndian(hexData + 3);
            *checksum = (hexData[hexSize - 1]);

            if ((*size + MIN_SIZE) == hexSize)
            {
                for (i = 0; i < *size; i++)
                {
                    rowData[i] = (hexData[DATA_OFFSET + i]);
                }
            }
            else
                err = CYRET_ERR_DATA;
        }
    }
    else
        err = CYRET_ERR_CMD;

    return err;
}

int CybtldrParse::CyBtldr_ParseRowData_v1(uint32_t bufSize, uint8_t* buffer, uint32_t* address, uint8_t* rowData, uint16_t* size, uint8_t* checksum)
{
    const uint16_t MIN_SIZE = 4; //4-addr
    const int DATA_OFFSET = 4;

    unsigned int i;
    uint16_t hexSize;
    uint8_t hexData[MAX_BUFFER_SIZE];
    int err = CYRET_SUCCESS;

    if (bufSize <= MIN_SIZE)
        err = CYRET_ERR_LENGTH;
    else if (buffer[0] == ':')
    {
        err = CyBtldr_FromAscii(bufSize - 1, &buffer[1], &hexSize, hexData);

        if (CYRET_SUCCESS == err)
        {
            *address = parse4ByteValueLittleEndian(hexData);
            *checksum = 0;

            if (MIN_SIZE < hexSize)
            {
                *size = hexSize - MIN_SIZE;
                for (i = 0; i < *size; i++)
                {
                    rowData[i] = (hexData[DATA_OFFSET + i]);
                    *checksum += rowData[i];
                }
            }
            else
                err = CYRET_ERR_DATA;
        }
    }
    else
        err = CYRET_ERR_CMD;

    return err;
}

int CybtldrParse::CyBtldr_ParseAppStartAndSize_v1(uint32_t* appStart, uint32_t* appSize, uint8_t* buf)
{
    long fp = ftell(dataFile);
    *appStart = 0xffffffff;
    *appSize = 0;
    uint32_t addr = 0;
    uint32_t rowLength;
    uint16_t rowSize;
    uint8_t rowData[MAX_BUFFER_SIZE];
    uint8_t checksum;
    int err = CYRET_SUCCESS;
    do
    {
        err = CyBtldr_ReadLine(&rowLength, (char*)buf);
        if (err == CYRET_SUCCESS && buf[0] == ':')
        {
            err = CyBtldr_ParseRowData_v1(rowLength, buf, &addr, rowData, &rowSize, &checksum);

            if (addr < (*appStart))
            {
                *appStart = addr;
            }
            (*appSize) += rowSize;
        }
    } while (err == CYRET_SUCCESS);
    if (err == CYRET_ERR_EOF)
        err = CYRET_SUCCESS;
    // reset to the file to where we were
    if (err == CYRET_SUCCESS)
    {
        fseek(dataFile, fp, SEEK_SET);
    }
    return err;
}

int CybtldrParse::CyBtldr_CloseDataFile(void)
{
    int err = 0;
    if (NULL != dataFile)
    {
        err = fclose(dataFile);
        dataFile = NULL;
    }
    return (0 == err)
        ? CYRET_SUCCESS
        : CYRET_ERR_FILE;
}
