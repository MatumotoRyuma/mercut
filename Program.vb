' Copyright 2022 松元隆磨

' Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at

'    http://www.apache.org/licenses/LICENSE-2.0
' Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

Option Infer On
Option Strict On

Imports System.Collections.Generic
Imports System.IO
Imports System.Math
Imports System.Runtime.InteropServices

Imports OpenCvSharp
Imports OpenCvSharp.Cv2

Module Program
    Sub Main(args As String())
        Dim latitude = 0.0
        Dim longitude = 0.0
        Dim outputFile = ""
        Dim i = 0
        
        ' Save input values.
        Try
            Dim parameters As New Dictionary(Of String, String)
            For i = 0 To args.Length - 1
                If args(i).StartsWith("-") Then parameters.Add(args(i), args(i + 1))               
            Next
            
            If Not File.Exists(args(0)) Then ShowUsage()
            latitude = Double.Parse(parameters("-lat"))
            longitude = Double.Parse(parameters("-lon"))
            outputFile = parameters("-out")
        Catch ex As KeyNotFoundException
            ShowUsage()
        Catch ex As System.FormatException
            ShowUsage()
        End Try
        
        ' Get a source image.
        Dim width, height As Integer
        Dim original() As Byte
        Using image = ImRead(args(0))
            width = image.Width
            height = image.Height
            
            ' Rotate longitude.
            longitude = - CInt(longitude) Mod 360
            If longitude < 0 Then longitude = 360 + longitude
            If longitude > 0 Then
                Using left As New Mat, right As New Mat
                    image({New Range(0, height), 
                           New Range(0, CInt(width * longitude / 360))}).
                        CopyTo(left)
                    image({New Range(0, height), 
                           New Range(CInt(width * longitude / 360) + 1, width)}).
                        CopyTo(right)
                    image({New Range(0, height), 
                           New Range(0, right.width)}) = right
                    image({New Range(0, height), 
                           New Range(right.width + 1, width)}) = left
                End Using
            End If
            
            ' Copy to an array.
            Redim original(image.width * image.height * 3 - 1)
            Marshal.Copy(image.Data, original, 0, width * height * 3)
        End Using

        ' The Mercator projection cannot express poles.
        Dim margin = (width - height) / 2 
        
        ' The image has BGR 3 Channels.
        Dim channelwidth = width * 3 
        
        ' Initiarize variables for a 3D globe.
        Dim r = CInt(width / (2 * Cv2.PI))
        Dim d = 2 * r
        Dim x = d * d * 3
        Dim y = d * 3
        Dim z = 3
        r -= 1
        Dim globe(d * d * d * 3 - 1) As Byte
        
        ' Create a 3D globe.
        i = 0
        Do While i < original.Length ' I used Do...Loop statement for avoiding bounds checking.
            ' Compute a coordinate on the globe.
            Dim theta = 2 * Cv2.PI * (i Mod channelwidth) / channelwidth
            Dim phai = Cv2.PI * (margin + i \ channelwidth) / width
            Dim positionX = CInt(r * (1 - Cos(phai)))
            Dim positionY = CInt(r * (1 - Cos(theta) * Sin(phai)))
            Dim positionZ = CInt(r * (1 - Sin(theta) * Sin(phai)))
            
            ' Visit to each pixels in a 11 x 11 surface that centre is position(X, Y, Z).
            Dim bias = positionZ * z + i Mod 3
            ' Y direction
            For j = -5 To 5
                Dim currentY = positionY + j
                If 0 <= currentY AndAlso currentY < d Then
                    Dim biasY = bias + currentY * y
                    
                    ' X direction
                    For k = -5 To 5
                        Dim currentX = positionX + k
                        If 0 <= currentX AndAlso currentX < d Then
                            Dim index = biasY + currentX * x
                            
                            ' Set a dot that is on the original 2D map to the pixel in the 11 x 11 surface.
                            If 0 <= index AndAlso index < globe.Length Then globe(index) = original(i)
                            
                        End If
                    Next
                End If
            Next
            i += 1
        Loop
        Print("A 3D map has been created.")

        ' Rotate latitude.
        Using globeMatrix As New Mat({d, d, d}, MatType.CV_8UC3),
              surface As New Mat(d, d, MatType.CV_8UC3),
              rotated As New Mat(d, d, MatType.CV_8UC3),
              affine = GetRotationMatrix2D(New Point2f(r, r), latitude, 1)
            Marshal.Copy(globe, 0, globeMatrix.Data, globe.Length)
            Dim vicinity As New Range(0, d)
            
            ' Rotate each surface.
            For i = 0 To d - 1
            
                ' Get a side surface from the 3D globe.
                Dim indexRange As New Range(i, i + 1)
                For j = 0 To surface.Cols - 1
                    ' Copy each row. N x N x 1 matrixes cannot convert to N x N matrixes directly.
                    surface({New Range(j, j + 1), vicinity}) = 
                        New Mat(1, d, MatType.CV_8UC3, globeMatrix({New Range(j, j + 1),
                                                                    indexRange,
                                                                    vicinity}).Data)
                Next
                
                ' Rotate the surface by Affine transform.
                WarpAffine(src:=surface, 
                           dst:=rotated, 
                           m:=affine, 
                           dsize:=surface.Size)
                
                ' Set the rotated image to the 3D globe.
                globeMatrix({vicinity, New Range(i, i + 1), vicinity}) = 
                    New Mat({d, 1, d}, MatType.CV_8UC3, rotated.Data)
            Next
            
            Marshal.Copy(globeMatrix.Data, globe, 0, globe.Length)
        End Using
        Print("Rotated.")

        ' Do the Mercator projection.
        Dim mercatorProjected(d * d - 1) As Vec3b
        i = 0
        Do While i < mercatorProjected.Length
            Dim theta = 2 * Cv2.PI * (i Mod d) / d
            Dim phai = Cv2.PI * (i \ d) / d
            Dim index = CInt(r * (1 - Cos(phai))) * x +
                        CInt(r * (1 - Cos(theta) * Sin(phai))) * y +
                        CInt(r * (1 - Sin(theta) * Sin(phai))) * z
            For j = 0 To 2
                mercatorProjected(i)(j) = globe(index + j)
            Next
            i += 1
        Loop
        Using result As New Mat(d, d, MatType.CV_8UC3, mercatorProjected)
            Print("The Mercator projection finished.")
            
            ImWrite(outputFile, result)
        End Using
    End Sub
    
    Sub ShowUsage()
        Print("Input values are not valid.")
        Print("Usage: .\mercut.exe worldmap.png -lat latitude -lon longitude -out output.png")
        End
    End Sub
    
    Sub Print(msg$)
        System.Console.WriteLine(msg)
    End Sub  
    
End Module
