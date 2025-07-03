using System.Runtime.InteropServices;

namespace AccessAPP.Services.HelperClasses
{
    public enum ReturnCodes
    {
        /// <summary>
        /// Completed successfully
        /// </summary>
        CYRET_SUCCESS = 0x00,
        /// <summary>
        /// File is not accessable
        /// </summary>
        CYRET_ERR_FILE = 0x01,
        /// <summary>
        /// Reached the end of the file
        /// </summary>
        CYRET_ERR_EOF = 0x02,
        /// <summary>
        /// The amount of data available is outside the expected range
        /// </summary>
        CYRET_ERR_LENGTH = 0x03,
        /// <summary>
        /// The data is not of the proper form
        /// </summary>
        CYRET_ERR_DATA = 0x04,
        /// <summary>
        /// The command is not recognized
        /// </summary>
        CYRET_ERR_CMD = 0x05,
        /// <summary>
        /// The expected device does not match the detected device
        /// </summary>
        CYRET_ERR_DEVICE = 0x06,
        /// <summary>
        /// The bootloader version detected is not supported
        /// </summary>
        CYRET_ERR_VERSION = 0x07,
        /// <summary>
        /// The checksum does not match the expected value
        /// </summary>
        CYRET_ERR_CHECKSUM = 0x08,
        /// <summary>
        /// The flash array is not valid
        /// </summary>
        CYRET_ERR_ARRAY = 0x09,
        /// <summary>
        /// The flash row is not valid
        /// </summary>
        CYRET_ERR_ROW = 0x0A,
        /// <summary>
        /// The bootloader is not ready to process data
        /// </summary>
        CYRET_ERR_BTLDR = 0x0B,
        /// <summary>
        /// The application is currently marked as active
        /// </summary>
        CYRET_ERR_ACTIVE = 0x0C,
        /// <summary>
        /// An unknown error occured
        /// </summary>
        CYRET_ERR_UNKNOWN = 0x0F,
        /// <summary>
        /// The operation was aborted
        /// </summary>
        CYRET_ABORT = 0xFF,

        /// <summary>
        /// The communications object reported an error
        /// </summary>
        CYRET_ERR_COMM_MASK = 0x2000,
        /// <summary>
        /// The bootloader reported an error
        /// </summary>
        CYRET_ERR_BTLDR_MASK = 0x4000,
        SUCCESS = 0x8000,
    }
    public class Bootloader_Utils
    {
        /// <summary>
        /// Structure used to pass communication data down to the unmanged native C code
        /// that handles the bootloading operations.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CyBtldr_CommunicationsData
        {
            /// <summary>
            /// Function used to open the communications connection
            /// </summary>
            public OpenConnection_USB OpenConnection;
            /// <summary>
            /// Function used to close the communications connection
            /// </summary>
            public CloseConnection_USB CloseConnection;
            /// <summary>
            /// Function used to read data over the communications connection
            /// </summary>
            public ReadData_USB ReadData;
            /// <summary>
            /// Function used to write data over the communications connection
            /// </summary>
            public WriteData_USB WriteData;
            /// <summary>
            /// Value used to specify the maximum number of bytes that can be trasfered at a time
            /// </summary>
            public uint MaxTransferSize;
        };

        /// <summary>
        /// Delegate used as a callback from native code for opening a communications connection
        /// </summary>
        /// <returns>Integer representing success == 0 or failure </returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int OpenConnection_USB();

        /// <summary>
        /// Delegate used as a callback from native code for closing a communications connection
        /// </summary>
        /// <returns>Integer representing success == 0 or failure </returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int CloseConnection_USB();

        /// <summary>
        /// Delegate used as a callback from native code for reading data from a communications connection
        /// </summary>
        /// <param name="buffer">The buffer to store the read data in</param>
        /// <param name="size">The number of bytes of data to read</param>
        /// <returns>Integer representing success == 0 or failure </returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ReadData_USB(IntPtr buffer, int size);

        /// <summary>
        /// Delegate used as a callback from native code for writing data over a communications connection
        /// </summary>
        /// <param name="buffer">The buffer containing data to write</param>
        /// <param name="size">The number of bytes to write</param>
        /// <returns>Integer representing success == 0 or failure </returns>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteData_USB(IntPtr buffer, int size);

        /// <summary>
        /// Delegate used as a callback from native code for notifying that a row is complete
        /// </summary>
        /// <param name="arrayID">The array ID that was accessed</param>
        /// <param name="rowNum">The row number within the array that was accessed</param>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void CyBtldr_ProgressUpdate(byte arrayID, ushort rowNum);

        [DllImport("C:\\Users\\HRS\\source\\repos\\AccessAPP\\AccessAPP\\obj\\Debug\\BootloaderUtilMultiThread.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int CyBtldr_Program([MarshalAs(UnmanagedType.LPStr)] string file, [MarshalAs(UnmanagedType.LPArray)] byte[] securityKey, byte appId, ref CyBtldr_CommunicationsData comm, CyBtldr_ProgressUpdate update);


    }
}
