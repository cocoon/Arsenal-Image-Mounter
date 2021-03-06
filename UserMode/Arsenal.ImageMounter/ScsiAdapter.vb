﻿
''''' ScsiAdapter.vb
''''' Class for controlling Arsenal Image Mounter Devices.
''''' 
''''' Copyright (c) 2012-2013, Arsenal Consulting, Inc. (d/b/a Arsenal Recon) <http://www.ArsenalRecon.com>
''''' This source code is available under the terms of the Affero General Public
''''' License v3.
'''''
''''' Please see LICENSE.txt for full license terms, including the availability of
''''' proprietary exceptions.
''''' Questions, comments, or requests for clarification: http://ArsenalRecon.com/contact/
'''''



''' <summary>
''' Represents Arsenal Image Mounter objects.
''' </summary>
Public Class ScsiAdapter
    Inherits DeviceObject

    Public Const CompatibleDriverVersion As UInteger = &H101

    Public Const AutoDeviceNumber As UInt32 = &HFFFFFF

    ''' <summary>
    ''' Object storing properties for a virtual disk device. Returned by
    ''' QueryDevice() method.
    ''' </summary>
    Public NotInheritable Class DeviceProperties

        Friend Sub New()

        End Sub

        ''' <summary>Device number of virtual disk.</summary>
        Public DeviceNumber As UInt32

        ''' <summary>Size of virtual disk.</summary>
        Public DiskSize As Int64

        ''' <summary>Number of bytes per sector for virtual disk geometry.</summary>
        Public BytesPerSector As UInt32

        ''' <summary>A skip offset if virtual disk data does not begin immediately at start of disk image file.
        ''' Frequently used with image formats like Nero NRG which start with a file header not used by Arsenal Image Mounter
        ''' or Windows filesystem drivers.</summary>
        Public ImageOffset As Int64

        ''' <summary>Flags specifying properties for virtual disk. See comments for each flag value.</summary>
        Public Flags As DeviceFlags

        ''' <summary>Name of disk image file holding storage for file type virtual disk or used to create a
        ''' virtual memory type virtual disk.</summary>
        Public Filename As String

    End Class

    Public ReadOnly ScsiPortNumber As Byte

    Private Shared Function OpenAdapterHandle(device As String, target As String) As SafeFileHandle

        Dim handle As SafeFileHandle
        Try
            handle = NativeFileIO.OpenFileHandle("\\?\" & device,
                                                 FileAccess.ReadWrite,
                                                 FileShare.ReadWrite,
                                                 FileMode.Open,
                                                 False)

        Catch ex As Exception
            Trace.WriteLine("PhDskMnt::OpenAdapterHandle: Error opening device '" & device & "' ('" & target & "'): " & ex.ToString())

            Return Nothing

        End Try

        Dim acceptedversion As Boolean
        For i = 1 To 3
            Try
                acceptedversion = CheckDriverVersion(handle)
                If acceptedversion Then
                    Return handle
                Else
                    handle.Dispose()
                    Throw New Exception("Incompatible version of Arsenal Image Mounter Miniport driver.")
                End If

            Catch ex As Exception
                Trace.WriteLine("PhDskMnt::OpenAdapterHandle: Error checking driver version: " & ex.ToString())

                '' In case of SCSIPORT (Win XP) miniport, there is always a risk
                '' that we lose contact with IOCTL_SCSI_MINIPORT after device adds
                '' and removes. Therefore, in case we know that we have a handle to
                '' the SCSI adapter and it fails IOCTL_SCSI_MINIPORT requests, just
                '' issue a bus re-enumeration to find the dummy IOCTL device, which
                '' will make SCSIPORT let control requests through again.
                If target.IndexOf("phdskmnt", StringComparison.InvariantCultureIgnoreCase) >= 0 Then
                    Trace.WriteLine("PhDskMnt::OpenAdapterHandle: Lost contact with miniport, rescanning...")
                    Try
                        API.RescanScsiAdapter()
                        Thread.Sleep(100)
                        Continue For

                    Catch ex2 As Exception
                        Trace.WriteLine("PhDskMnt::RescanScsiAdapter: " & ex2.ToString())

                    End Try
                End If
                handle.Dispose()
                Return Nothing

            End Try
        Next

        Return Nothing

    End Function

    ''' <summary>
    ''' Retrieves a handle to first found adapter, or null if error occurs.
    ''' </summary>
    ''' <remarks>Arsenal Image Mounter does not currently support more than one adapter.</remarks>
    ''' <returns>A structure containing SCSI port number and an open handle to first found
    ''' compatible adapter.</returns>
    Private Shared Function OpenAdapter() As KeyValuePair(Of Byte, SafeFileHandle)

        Dim firstfound =
          Aggregate dosdevice In NativeFileIO.QueryDosDevice()
            Where
              dosdevice.StartsWith("Scsi", StringComparison.OrdinalIgnoreCase) AndAlso
              dosdevice.EndsWith(":")
            Let
              target = NativeFileIO.QueryDosDevice(dosdevice).FirstOrDefault()
            Where
              target IsNot Nothing AndAlso
              (target.StartsWith("\Device\Scsi\phdskmnt", StringComparison.OrdinalIgnoreCase) OrElse
               target.StartsWith("\Device\RaidPort", StringComparison.OrdinalIgnoreCase))
            Let
              handle = OpenAdapterHandle(dosdevice, target)
            Into
              FirstOrDefault(handle IsNot Nothing)

        If firstfound Is Nothing Then
            Throw New FileNotFoundException("No Arsenal Image Mounter adapter found")
        End If

        Return New KeyValuePair(Of Byte, SafeFileHandle)(Byte.Parse(firstfound.dosdevice.Substring(4, firstfound.dosdevice.Length - 5)), firstfound.handle)

    End Function

    ''' <summary>
    ''' Opens first found Arsenal Image Mounter.
    ''' </summary>
    Public Sub New()
        Me.New(OpenAdapter())

    End Sub

    Private Sub New(OpenAdapterHandle As KeyValuePair(Of Byte, SafeFileHandle))
        MyBase.New(OpenAdapterHandle.Value, FileAccess.ReadWrite)

        Me.ScsiPortNumber = OpenAdapterHandle.Key

        Trace.WriteLine("Successfully opened adapter with SCSI portnumber = " & ScsiPortNumber & ".")
    End Sub

    ''' <summary>
    ''' Opens a specific Arsenal Image Mounter.
    ''' </summary>
    ''' <param name="ScsiPortNumber">Scsi adapter port number as assigned by SCSI class driver.</param>
    Public Sub New(ScsiPortNumber As Byte)
        MyBase.New("\\?\Scsi" & ScsiPortNumber & ":", FileAccess.ReadWrite)

        Me.ScsiPortNumber = ScsiPortNumber

        Trace.WriteLine("Successfully opened adapter with SCSI portnumber = " & ScsiPortNumber & ".")

        If Not CheckDriverVersion() Then
            Throw New Exception("Incompatible version of Arsenal Image Mounter Miniport driver.")
        End If

    End Sub

    ''' <summary>
    ''' Retrieves a list of virtual disks on this adapter. Each element in returned list holds device number of an existing
    ''' virtual disk.
    ''' </summary>
    Public Function GetDeviceList() As List(Of UInt32)

        Dim ReturnCode As Int32

        Dim Response =
          NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                      NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_QUERY_ADAPTER,
                                                      0,
                                                      Sub(data) data.Write(New Byte(0 To 65535) {}),
                                                      ReturnCode)

        If ReturnCode <> 0 Then
            Throw NativeFileIO.GetExceptionForNtStatus(ReturnCode)
        End If

        Dim NumberOfDevices = Response.ReadInt32()
        Dim DeviceList As New List(Of UInt32)(NumberOfDevices)
        For i = 1 To NumberOfDevices
            DeviceList.Add(Response.ReadUInt32())
        Next
        Return DeviceList

    End Function

    ''' <summary>
    ''' Retrieves a list of DeviceProperties objects for each virtual disk on this adapter.
    ''' </summary>
    Public Function GetDeviceProperties() As List(Of DeviceProperties)

        Return GetDeviceList().ConvertAll(AddressOf QueryDevice)

    End Function

    ''' <summary>
    ''' Creates a new virtual disk.
    ''' </summary>
    ''' <param name="DiskSize">Size of virtual disk. If this parameter is zero, current size of disk image file will
    ''' automatically be used as virtual disk size.</param>
    ''' <param name="BytesPerSector">Number of bytes per sector for virtual disk geometry. This parameter can be zero
    '''  in which case most reasonable value will be automatically used by the driver.</param>
    ''' <param name="ImageOffset">A skip offset if virtual disk data does not begin immediately at start of disk image file.
    ''' Frequently used with image formats like Nero NRG which start with a file header not used by Arsenal Image Mounter
    ''' or Windows filesystem drivers.</param>
    ''' <param name="Flags">Flags specifying properties for virtual disk. See comments for each flag value.</param>
    ''' <param name="Filename">Name of disk image file to use or create. If disk image file already exists, the DiskSize
    ''' parameter can be zero in which case current disk image file size will be used as virtual disk size. If Filename
    ''' paramter is Nothing/null disk will be created in virtual memory and not backed by a physical disk image file.</param>
    ''' <param name="NativePath">Specifies whether Filename parameter specifies a path in Windows native path format, the
    ''' path format used by drivers in Windows NT kernels, for example \Device\Harddisk0\Partition1\imagefile.img. If this
    ''' parameter is False path in FIlename parameter will be interpreted as an ordinary user application path.</param>
    ''' <param name="DeviceNumber">In: Device number for device to create. Device number must not be in use by an existing
    ''' virtual disk. For automatic allocation of device number, pass ScsiAdapter.AutoDeviceNumber.
    '''
    ''' Out: Device number for created device.</param>
    Public Sub CreateDevice(DiskSize As Int64,
                            BytesPerSector As UInt32,
                            ImageOffset As Int64,
                            Flags As DeviceFlags,
                            Filename As String,
                            NativePath As Boolean,
                            ByRef DeviceNumber As UInt32)

        '' Temporary variable for passing through lambda function
        Dim devnr = DeviceNumber

        '' Both UInt32.MaxValue and AutoDeviceNumber can be used
        '' for auto-selecting device number, but only AutoDeviceNumber
        '' is accepted by driver.
        If devnr = UInteger.MaxValue Then
            devnr = AutoDeviceNumber
        End If

        '' Translate Win32 path to native NT path that kernel understands
        If (Not String.IsNullOrEmpty(Filename)) AndAlso (Not NativePath) Then
            Select Case API.GetProxyType(Flags)

                Case DeviceFlags.ProxyTypeSharedMemory
                    Filename = "\BaseNamedObjects\Global\" & Filename

                Case DeviceFlags.ProxyTypeComm, DeviceFlags.ProxyTypeTCP

                Case Else
                    Filename = NativeFileIO.GetNtPath(Filename)

            End Select
        End If

        '' Show what we got
        Trace.WriteLine("ScsiAdapter.CreateDevice: Native filename='" & Filename & "'")

        Dim FillRequestData =
          Sub(Request As BinaryWriter)
              Request.Write(devnr)
              Request.Write(DiskSize)
              Request.Write(BytesPerSector)
              Request.Write(0UI)
              Request.Write(ImageOffset)
              Request.Write(CUInt(Flags))
              If String.IsNullOrEmpty(Filename) Then
                  Request.Write(0US)
              Else
                  Dim bytes = Encoding.Unicode.GetBytes(Filename)
                  Request.Write(CUShort(bytes.Length))
                  Request.Write(bytes)
              End If
          End Sub

        Dim ReturnCode As Int32

        Dim Response =
          NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                      NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_CREATE_DEVICE,
                                                      0,
                                                      FillRequestData,
                                                      ReturnCode)

        If ReturnCode <> 0 Then
            Throw NativeFileIO.GetExceptionForNtStatus(ReturnCode)
        End If

        DeviceNumber = Response.ReadUInt32()
        DiskSize = Response.ReadInt64()
        BytesPerSector = Response.ReadUInt32
        ImageOffset = Response.ReadInt64()
        Flags = CType(Response.ReadUInt32(), DeviceFlags)

        While Not GetDeviceList().Contains(DeviceNumber)
            Trace.WriteLine("Waiting for new device " & DeviceNumber.ToString("X6") & " to be registered by driver...")
            Thread.Sleep(500)
        End While

        Dim ScsiAddress As New NativeFileIO.Win32API.SCSI_ADDRESS(ScsiPortNumber, DeviceNumber)
        Dim DiskDevice As DiskDevice

        Do

            Thread.Sleep(500)

            Try
                DiskDevice = New DiskDevice(ScsiAddress, FileAccess.Read)
                Exit Do

            Catch ex As Exception
                Trace.WriteLine("Error opening device: " & ex.ToString())
                Thread.Sleep(500)

            End Try

            Trace.WriteLine("Not ready, rescanning SCSI adapter...")
            API.RescanScsiAdapter()

        Loop

        Using DiskDevice

            '' Wait at most 20 x 500 msec for device to get initialized by driver
            For i = 1 To 20

                Thread.Sleep(500)
                Trace.WriteLine("Updating disk properties...")
                DiskDevice.UpdateProperties()

                If DiskDevice.DiskSize <> 0 Then
                    Exit For
                End If

            Next

        End Using

        Trace.WriteLine("CreateDevice done.")

    End Sub

    ''' <summary>
    ''' Removes an existing virtual disk from adapter.
    ''' </summary>
    ''' <param name="DeviceNumber">Device number to remove. Note that AutoDeviceNumber constant passed
    ''' in this parameter causes all present virtual disks to be removed from this adapter.</param>
    Public Sub RemoveDevice(DeviceNumber As UInt32)

        Dim FillRequestData =
          Sub(Request As BinaryWriter)
              Request.Write(DeviceNumber)
          End Sub

        Dim ReturnCode As Int32

        Dim Response =
          NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                      NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_REMOVE_DEVICE,
                                                      0,
                                                      FillRequestData,
                                                      ReturnCode)

        If ReturnCode <> 0 Then
            Throw NativeFileIO.GetExceptionForNtStatus(ReturnCode)
        End If

    End Sub

    ''' <summary>
    ''' Removes all virtual disks on current adapter.
    ''' </summary>
    Public Sub RemoveAllDevices()

        RemoveDevice(AutoDeviceNumber)

    End Sub

    ''' <summary>
    ''' Retrieves properties for an existing virtual disk.
    ''' </summary>
    ''' <param name="DeviceNumber">Device number of virtual disk to retrieve properties for.</param>
    ''' <param name="DiskSize">Size of virtual disk.</param>
    ''' <param name="BytesPerSector">Number of bytes per sector for virtual disk geometry.</param>
    ''' <param name="ImageOffset">A skip offset if virtual disk data does not begin immediately at start of disk image file.
    ''' Frequently used with image formats like Nero NRG which start with a file header not used by Arsenal Image Mounter
    ''' or Windows filesystem drivers.</param>
    ''' <param name="Flags">Flags specifying properties for virtual disk. See comments for each flag value.</param>
    ''' <param name="Filename">Name of disk image file holding storage for file type virtual disk or used to create a
    ''' virtual memory type virtual disk.</param>
    Public Sub QueryDevice(DeviceNumber As UInt32,
                           ByRef DiskSize As Int64,
                           ByRef BytesPerSector As UInt32,
                           ByRef ImageOffset As Int64,
                           ByRef Flags As DeviceFlags,
                           ByRef Filename As String)

        Dim FillRequestData =
          Sub(Request As BinaryWriter)
              Request.Write(DeviceNumber)
              Request.Write(0L)
              Request.Write(0UI)
              Request.Write(0L)
              Request.Write(0UI)
              Request.Write(65535US)
              Request.Write(New Byte(0 To 65534) {})
          End Sub

        Dim ReturnCode As Int32

        Dim Response = NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                                   NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_QUERY_DEVICE,
                                                                   0,
                                                                   FillRequestData,
                                                                   ReturnCode)

        '' STATUS_OBJECT_NAME_NOT_FOUND. Possible "zombie" device, just return empty data.
        If ReturnCode = &HC0000034I Then
            Return
        ElseIf ReturnCode <> 0 Then
            Throw NativeFileIO.GetExceptionForNtStatus(ReturnCode)
        End If

        DeviceNumber = Response.ReadUInt32()
        DiskSize = Response.ReadInt64()
        BytesPerSector = Response.ReadUInt32
        Response.ReadUInt32()
        ImageOffset = Response.ReadInt64()
        Flags = CType(Response.ReadUInt32(), DeviceFlags)
        Dim FilenameLength = Response.ReadUInt16()
        If FilenameLength = 0 Then
            Filename = Nothing
        Else
            Filename = Encoding.Unicode.GetString(Response.ReadBytes(FilenameLength))
        End If

    End Sub

    ''' <summary>
    ''' Retrieves properties for an existing virtual disk.
    ''' </summary>
    ''' <param name="DeviceNumber">Device number of virtual disk to retrieve properties for.</param>
    Public Function QueryDevice(DeviceNumber As UInt32) As DeviceProperties

        Dim DeviceProperties As New DeviceProperties With {
          .DeviceNumber = DeviceNumber
        }

        QueryDevice(DeviceNumber,
                    DeviceProperties.DiskSize,
                    DeviceProperties.BytesPerSector,
                    DeviceProperties.ImageOffset,
                    DeviceProperties.Flags,
                    DeviceProperties.Filename)
        Return DeviceProperties

    End Function

    ''' <summary>
    ''' Modifies properties for an existing virtual disk.
    ''' </summary>
    ''' <param name="DeviceNumber">Device number of virtual disk to modify properties for.</param>
    ''' <param name="FlagsToChange">Flags for which to change values for.</param>
    ''' <param name="FlagValues">New flag values.</param>
    Public Sub ChangeFlags(DeviceNumber As UInt32,
                           FlagsToChange As DeviceFlags,
                           FlagValues As DeviceFlags)

        Dim FillRequestData =
          Sub(Request As BinaryWriter)
              Request.Write(DeviceNumber)
              Request.Write(CUInt(FlagsToChange))
              Request.Write(CUInt(FlagValues))
          End Sub

        Dim ReturnCode As Int32

        Dim Response = NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                                   NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_SET_DEVICE_FLAGS,
                                                                   0,
                                                                   FillRequestData,
                                                                   ReturnCode)

        If ReturnCode <> 0 Then
            Throw NativeFileIO.GetExceptionForNtStatus(ReturnCode)
        End If

    End Sub

    ''' <summary>
    ''' Checks if version of running Arsenal Image Mounter SCSI miniport servicing this device object is compatible with this API
    ''' library. If this device object is not created by Arsenal Image Mounter SCSI miniport, an exception is thrown.
    ''' </summary>
    Public Function CheckDriverVersion() As Boolean

        Return CheckDriverVersion(SafeFileHandle)

    End Function

    ''' <summary>
    ''' Checks if version of running Arsenal Image Mounter SCSI miniport servicing this device object is compatible with this API
    ''' library. If this device object is not created by Arsenal Image Mounter SCSI miniport, an exception is thrown.
    ''' </summary>
    Public Shared Function CheckDriverVersion(SafeFileHandle As SafeFileHandle) As Boolean

        Dim ReturnCode As Int32
        Dim Response = NativeFileIO.PhDiskMntCtl.SendSrbIoControl(SafeFileHandle,
                                                                  NativeFileIO.PhDiskMntCtl.SMP_IMSCSI_QUERY_VERSION,
                                                                  0,
                                                                  Nothing,
                                                                  ReturnCode)

        If ReturnCode = CompatibleDriverVersion Then
            Return True
        End If

        Trace.WriteLine("Library version: " & CompatibleDriverVersion.ToString("X4"))
        Trace.WriteLine("Driver version: " & ReturnCode.ToString("X4"))

        Return False

    End Function

    ''' <summary>
    ''' Re-enumerates partitions on all disk drives currently connected to this adapter. No
    ''' exceptions are thrown on error, but any exceptions from underlying API calls are logged
    ''' to trace log.
    ''' </summary>
    Public Sub UpdateDiskProperties()

        For Each ScsiAddress In
            From DeviceNumber In GetDeviceList()
            Select New NativeFileIO.Win32API.SCSI_ADDRESS(ScsiPortNumber, DeviceNumber)

            NativeFileIO.UpdateDiskProperties(ScsiAddress)
        Next

    End Sub

    ''' <summary>
    ''' Re-enumerates partitions on specified disk currently connected to this adapter. No
    ''' exceptions are thrown on error, but any exceptions from underlying API calls are logged
    ''' to trace log.
    ''' </summary>
    Public Sub UpdateDiskProperties(DeviceNumber As UInteger)

        Dim ScsiAddress As New NativeFileIO.Win32API.SCSI_ADDRESS(ScsiPortNumber, DeviceNumber)

        NativeFileIO.UpdateDiskProperties(ScsiAddress)

    End Sub

End Class

