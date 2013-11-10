// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.

open System
open ExcelParser

[<EntryPoint>]
let main argv = 
    if argv.Length <> 5 then
        printfn "Usage: ConsoleApp [AWS key] [AWS secret] [s3 bucket] [output filename] [input dictionary GUID]"
        1
    else
        let aws_id = argv.[0]
        let aws_secret = argv.[1]
        let bucket = argv.[2]
        let filename = argv.[3]
        let input_guid = Guid.Parse(argv.[4])

        printfn "Creating job..."
        let job = TyPissed.Job(aws_id, bucket)

        printfn "Getting input data from S3..."
        let data = job.DeserializeInputsFromS3(input_guid, aws_secret)

        printfn "Rendering bitmaps and uploading to S3..."
        job.UploadAllImages(aws_secret)

        printfn "Writing to %s" filename
        job.WriteJobToCSV(filename)

        printfn "Uploading job state to S3..."
        let state = (job.SerializeToS3(aws_secret))
        printfn "Job state ID is: %s" state

        printfn "Downloading job state from S3..."
        let job2 = TyPissed.Job.DeserializeFromS3(bucket, state, aws_id, aws_secret)

        printfn "Deserialized job contains the following images:\n%s" (String.Join("\n", job2.GetInternalData()))

        printfn "Done."
        0 // return an integer exit code
