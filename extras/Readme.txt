In order to build the library in Linux environment, follow the following steps 

1. first of all SSH in to the cassia container

2. Make a folder "local_lib_compile" or with any name you would like 

3. Now using SFTP copy the files from the solution folder of (BootloaderUtilMultiThread) to the folder created in linux.
	The files to be copied are
	- BootloaderUtilMultiThread.cpp  
	- BootloaderUtilMultiThread.h  
	- CybtldrApi1.cpp  
	- CybtldrApi1.h  
	- CybtldrApi2.cpp  
	- CybtldrApi2.h  
	- CybtldrCommand.cpp  
	- CybtldrCommand.h  
	- CybtldrParse.cpp  
	- CybtldrParse.h  
	- cybtldr_utils.h

4. now in the Linux terminal run the following 


	gcc -shared -o libBootloaderUtilMultiThread.so -fPIC BootloaderUtilMultiThread.cpp CybtldrApi1.cpp CybtldrApi2.cpp CybtldrCommand.cpp CybtldrParse.cpp

5. Then you need to copy your built library "libBootloaderUtilMultiThread.so" to your AccessApp build solution. Below is an example of how to copy this library to your solution.

	cp ../local_lib_compile/libBootloaderUtilMultiThread.so ./libBootloaderUtilMultiThread.so