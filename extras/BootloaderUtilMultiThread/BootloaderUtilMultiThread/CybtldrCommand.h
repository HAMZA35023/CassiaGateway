#pragma once

#include <stdint.h>
#include "cybtldr_utils.h"

/* Maximum number of bytes to allocate for a single command.  */
#define MAX_COMMAND_SIZE 512


//STANDARD PACKET FORMAT:
// Multi byte entries are encoded in LittleEndian.
/*******************************************************************************
* [1-byte] [1-byte ] [2-byte] [n-byte] [ 2-byte ] [1-byte]
* [ SOP  ] [Command] [ Size ] [ Data ] [Checksum] [ EOP  ]
*******************************************************************************/


/* The first byte of any boot loader command. */
#define CMD_START               0x01
/* The last byte of any boot loader command. */
#define CMD_STOP                0x17
/* The minimum number of bytes in a bootloader command. */
#define BASE_CMD_SIZE           0x07

/* Command identifier for verifying the checksum value of the bootloadable project. */
#define CMD_VERIFY_CHECKSUM     0x31
/* Command identifier for getting the number of flash rows in the target device. */
#define CMD_GET_FLASH_SIZE      0x32
/* Command identifier for getting info about the app status. This is only supported on multi app bootloader. */
#define CMD_GET_APP_STATUS      0x33
/* Command identifier for erasing a row of flash data from the target device. */
#define CMD_ERASE_ROW           0x34
/* Command identifier for making sure the bootloader host and bootloader are in sync. */
#define CMD_SYNC                0x35
/* Command identifier for setting the active application. This is only supported on multi app bootloader. */
#define CMD_SET_ACTIVE_APP      0x36
/* Command identifier for sending a block of data to the bootloader without doing anything with it yet. */
#define CMD_SEND_DATA           0x37
/* Command identifier for starting the boot loader.  All other commands ignored until this is sent. */
#define CMD_ENTER_BOOTLOADER    0x38
/* Command identifier for programming a single row of flash. */
#define CMD_PROGRAM_ROW         0x39
/* Command identifier for verifying the contents of a single row of flash. */
#define CMD_GET_ROW_CHECKSUM    0x3A
/* Command identifier for exiting the bootloader and restarting the target program. */
#define CMD_EXIT_BOOTLOADER     0x3B
/* Command to erase data */
#define CMD_ERASE_DATA          0x44
/* Command to program data. */
#define CMD_PROGRAM_DATA        0x49
/* Command to verify data */
#define CMD_VERIFY_DATA         0x4A
/* Command to set application metadata in bootloader SDK */
#define CMD_SET_METADATA        0x4C
/* Command to set encryption initial vector */
#define CMD_SET_EIV              0x4D

/*
 * This enum defines the different types of checksums that can be
 * used by the bootloader for ensuring data integrity.
 */
typedef enum
{
    /* Checksum type is a basic inverted summation of all bytes */
    SUM_CHECKSUM = 0x00,
    /* 16-bit CRC checksum using the CCITT implementation */
    CRC_CHECKSUM = 0x01,
} CyBtldr_ChecksumType;

class CybtldrCommand
{
protected:
    CyBtldr_ChecksumType CyBtldr_Checksum;
public:

    CybtldrCommand();

    /*******************************************************************************
    * Function Name: CyBtldr_ComputeChecksum16bit
    ********************************************************************************
    * Summary:
    *   Computes the 2 byte checksum for the provided command data.  The checksum is
    *   either 2's complement or CRC16.
    *
    * Parameters:
    *   buf  - The data to compute the checksum on
    *   size - The number of bytes contained in buf.
    *
    * Returns:
    *   The checksum for the provided data.
    *
    *******************************************************************************/
    uint16_t CyBtldr_ComputeChecksum16bit(uint8_t* buf, uint32_t size);

    /*******************************************************************************
    * Function Name: CyBtldr_ComputeChecksum32bit
    ********************************************************************************
    * Summary:
    *   Computes the 4 byte checksum for the provided command data.  The checksum is
    *   computed using CRC32-C
    *
    * Parameters:
    *   buf  - The data to compute the checksum on
    *   size - The number of bytes contained in buf.
    *
    * Returns:
    *   The checksum for the provided data.
    *
    *******************************************************************************/
    uint32_t CyBtldr_ComputeChecksum32bit(uint8_t* buf, uint32_t size);


    /*******************************************************************************
    * Function Name: CyBtldr_SetCheckSumType
    ********************************************************************************
    * Summary:
    *   Updates what checksum algorithm is used when generating packets
    *
    * Parameters:
    *   chksumType - The type of checksum to use when creating packets
    *
    * Returns:
    *   NA
    *
    *******************************************************************************/
    void CyBtldr_SetCheckSumType(CyBtldr_ChecksumType chksumType);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseDefaultCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from any command that returns the default result packet
    *   data.  The default result is just a status byte
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   status  - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseDefaultCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateEnterBootLoaderCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to startup the bootloader.
    *   NB: This command must be sent before the bootloader will respond to any
    *       other command.
    *
    * Command Size: 13
    *
    * Parameters:
    *   protect         - The flash protection settings.
    *   cmdBuf          - The preallocated buffer to store command data in.
    *   cmdSize         - The number of bytes in the command.
    *   resSize         - The number of bytes expected in the bootloader's response packet.
    *   securityKeyBuff - The 6 byte or null bootloader security key
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateEnterBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize, const uint8_t* securityKeyBuff);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateEnterBootLoaderCmd_v1
    ********************************************************************************
    * Summary:
    *   Creates the command used to startup the bootloader.
    *   NB: This command must be sent before the bootloader will respond to any
    *       other command.
    *
    * Command Size: 13
    *
    * Parameters:
    *   protect         - The flash protection settings.
    *   cmdBuf          - The preallocated buffer to store command data in.
    *   cmdSize         - The number of bytes in the command.
    *   resSize         - The number of bytes expected in the bootloader's response packet.
    *   productID       - The product ID of the cyacd
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateEnterBootLoaderCmd_v1(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize, uint32_t productID);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseEnterBootLoaderCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the EnterBootLoader command to get the resultant
    *   data.
    *
    * Response Size: 15
    *
    * Parameters:
    *   cmdBuf     - The buffer containing the output from the bootloader.
    *   cmdSize    - The number of bytes in cmdBuf.
    *   siliconId  - The silicon ID of the device being communicated with.
    *   siliconRev - The silicon Revision of the device being communicated with.
    *   blVersion  - The bootloader version being communicated with.
    *   status     - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseEnterBootLoaderCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint32_t* siliconId, uint8_t* siliconRev, uint32_t* blVersion, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateExitBootLoaderCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to stop communicating with the boot loader and to
    *   trigger the target device to restart, running the new bootloadable
    *   application.
    *
    * Command Size: 7
    *
    * Parameters:
    *   cmdBuf    - The preallocated buffer to store command data in.
    *   cmdSize   - The number of bytes in the command.
    *   resSize   - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateExitBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateProgramRowCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to program a single flash row.
    *
    * Command Size: At least 10
    *
    * Parameters:
    *   arrayId - The array id to program.
    *   rowNum  - The row number to program.
    *   buf     - The buffer of data to program into the flash row.
    *   size    - The number of bytes in data for the row.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateProgramRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseProgramRowCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the ProgramRow command to get the resultant
    *   data.
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   status  - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseProgramRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateVerifyRowCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to verify that the contents of flash match the
    *   provided row data.
    *
    * Command Size: At least 10
    *
    * Parameters:
    *   arrayId - The array id to verify.
    *   rowNum  - The row number to verify.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateVerifyRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseVerifyRowCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the VerifyRow command to get the resultant
    *   data.
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf   - The preallocated buffer to store command data in.
    *   cmdSize  - The number of bytes in the command.
    *   checksum - The checksum from the row to verify.
    *   status   - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseVerifyRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* checksum, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateEraseRowCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to erase a single flash row.
    *
    * Command Size: 10
    *
    * Parameters:
    *   arrayId - The array id to erase.
    *   rowNum  - The row number to erase.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateEraseRowCmd(uint8_t arrayId, uint16_t rowNum, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseEraseRowCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the EraseRow command to get the resultant
    *   data.
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   status  - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseEraseRowCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateVerifyChecksumCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to verify that the checksum value in flash matches
    *   what is expected.
    *
    * Command Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateVerifyChecksumCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseVerifyChecksumCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the VerifyChecksum command to get the resultant
    *   data.
    *
    * Response Size: 8
    *
    * Parameters:
    *   cmdBuf           - The preallocated buffer to store command data in.
    *   cmdSize          - The number of bytes in the command.
    *   checksumValid    - Whether or not the full checksums match (1 = valid, 0 = invalid)
    *   status           - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseVerifyChecksumCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* checksumValid, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateGetFlashSizeCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to retrieve the number of flash rows in the device.
    *
    * Command Size: 8
    *
    * Parameters:
    *   arrayId - The array ID to get the flash size of.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateGetFlashSizeCmd(uint8_t arrayId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseGetFlashSizeCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the GetFlashSize command to get the resultant
    *   data.
    *
    * Response Size: 11
    *
    * Parameters:
    *   cmdBuf   - The preallocated buffer to store command data in.
    *   cmdSize  - The number of bytes in the command.
    *   startRow - The first available row number in the flash array.
    *   endRow   - The last available row number in the flash array.
    *   status   - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseGetFlashSizeCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint16_t* startRow, uint16_t* endRow, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateSendDataCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to send a block of data to the target.
    *
    * Command Size: greater than 7
    *
    * Parameters:
    *   buf     - The buffer of data to program into the flash row.
    *   size    - The number of bytes in data for the row.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateSendDataCmd(uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseSendDataCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the SendData command to get the resultant
    *   data.
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   status  - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseSendDataCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateSyncBootLoaderCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to ensure that the host application is in sync
    *   with the bootloader application.
    *
    * Command Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateSyncBootLoaderCmd(uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateGetAppStatusCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to get information about the application.  This
    *   command is only supported by the multi application bootloader.
    *
    * Command Size: 8
    *
    * Parameters:
    *   appId   - The id for the application to get status for
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateGetAppStatusCmd(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseGetAppStatusCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the GetAppStatus command to get the resultant
    *   data.
    *
    * Response Size: 9
    *
    * Parameters:
    *   cmdBuf       - The preallocated buffer to store command data in.
    *   cmdSize      - The number of bytes in the command.
    *   isValid      - Is the application valid.
    *   isActive     - Is the application currently marked as active.
    *   status       - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseGetAppStatusCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* isValid, uint8_t* isActive, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateSetActiveAppCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to set the active application for the bootloader
    *   to run.  This command is only supported by the multi application
    *   bootloader.
    *
    * Command Size: 8
    *
    * Parameters:
    *   appId   - The id for the application to get status for
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateSetActiveAppCmd(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_ParseSetActiveAppCmdResult
    ********************************************************************************
    * Summary:
    *   Parses the output from the SetActiveApp command to get the resultant
    *   data.
    *
    * Response Size: 7
    *
    * Parameters:
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   status  - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS    - The command was constructed successfully
    *   CYRET_ERR_LENGTH - The packet does not contain enough data
    *   CYRET_ERR_DATA   - The packet's contents are not correct
    *
    *******************************************************************************/
    int CyBtldr_ParseSetActiveAppCmdResult(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t* status);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateProgramDataCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to program data.
    *
    * Command Size: At least 15
    *
    * Parameters:
    *   address - The address to program.
    *   chksum  - The checksum all the data being programmed by this command the preceding send data commands.
    *   buf     - The buffer of data to program into the flash row.
    *   size    - The number of bytes in data for the row.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateProgramDataCmd(uint32_t address, uint32_t chksum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateVerifyDataCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to verify data.
    *
    * Command Size: At least 15
    *
    * Parameters:
    *   address - The address to verify.
    *   chksum  - The checksum all the data being verified by this command the preceding send data commands.
    *   buf     - The buffer of data to verify against.
    *   size    - The number of bytes in data for the row.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateVerifyDataCmd(uint32_t address, uint32_t chksum, uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateEraseDataCmd
    ********************************************************************************
    * Summary:
    *   Creates the command used to erase data.
    *
    * Command Size: 11
    *
    * Parameters:
    *   address     - The address to erase.
    *   cmdBuf      - The preallocated buffer to store command data in.
    *   cmdSize     - The number of bytes in the command.
    *   resSize     - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateEraseDataCmd(uint32_t address, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateVerifyChecksumCmd_v1
    ********************************************************************************
    * Summary:
    *   Creates the command used to verify application.
    *
    * Command Size: 8
    *
    * Parameters:
    *   appId   - The application number.
    *   cmdBuf  - The preallocated buffer to store command data in.
    *   cmdSize - The number of bytes in the command.
    *   resSize - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS  - The command was constructed successfully
    *
    *******************************************************************************/
    int CyBtldr_CreateVerifyChecksumCmd_v1(uint8_t appId, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateSetApplicationMetadataCmd
    ********************************************************************************
    * Summary:
    *   Set the bootloader SDK's metadata field for a specific application ID
    *
    * Command Size: 16
    *
    * Parameters:
    *   appID       - The ID number of the application.
    *   buf         - The buffer containing the application metadata (8 bytes).
    *   cmdBuf      - The preallocated buffer to store the command data in.
    *   cmdSize     - The number of bytes in the command.
    *   resSize     - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS -The command was constructed successfully
    *******************************************************************************/
    int CyBtldr_CreateSetApplicationMetadataCmd(uint8_t appID, uint8_t* buf, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_CreateSetEncryptionInitialVectorCmd
    ********************************************************************************
    * Summary:
    *   Set the bootloader SDK's encryption initial vector (EIV)
    *
    * Command Size: 7 or 15 or 23
    *
    * Parameters:
    *   buf         - The buffer containing the EIV.
    *   size        - The number bytes of the EIV. (Should be 0 or 8 or 16)
    *   cmdBuf      - The preallocated buffer to store the command data in.
    *   cmdSize     - The number of bytes in the command.
    *   resSize     - The number of bytes expected in the bootloader's response packet.
    *
    * Returns:
    *   CYRET_SUCCESS -The command was constructed successfully
    *******************************************************************************/
    int CyBtldr_CreateSetEncryptionInitialVectorCmd(uint8_t* buf, uint16_t size, uint8_t* cmdBuf, uint32_t* cmdSize, uint32_t* resSize);

    /*******************************************************************************
    * Function Name: CyBtldr_TryParseParketStatus
    ********************************************************************************
    * Summary:
    *   Parses the output packet data
    *
    * Parameters:
    *   packet      - The preallocated buffer to store command data in.
    *   packetSize  - The number of bytes in the command.
    *   status      - The status code returned by the bootloader.
    *
    * Returns:
    *   CYRET_SUCCESS           - The packet is a valid packet
    *   CYBTLDR_STAT_ERR_UNK    - The packet is not a valid packet
    *
    *******************************************************************************/
    int CyBtldr_TryParseParketStatus(uint8_t* packet, int packetSize, uint8_t* status);

    int CreateCmd(uint8_t* cmdBuf, uint32_t cmdSize, uint8_t cmdCode);
};

