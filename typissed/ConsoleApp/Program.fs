// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.

open System
open ExcelParser

[<EntryPoint>]
let main argv = 
    if argv.Length <> 4 then
        printfn "Usage: ConsoleApp [AWS key] [AWS secret] [s3 bucket] [filename]"
        1
    else
        let aws_id = argv.[0]
        let aws_secret = argv.[1]
        let bucket = argv.[2]
        let filename = argv.[3]

        printfn "Creating job..."
        let job = TyPissed.Job(aws_id, bucket)

        printfn "Adding sample strings to job..."
        let a1 = AST.Address.FromR1C1(1,1,"myworkbook","myworksheet","foobar")
        let s1 = "2342345705"
        job.AddInput(a1, s1)

        let a2 = AST.Address.FromR1C1(4,1,"myworkbook","myworksheet","foobar")
        let s2 = "435jhkjfd9"
        job.AddInput(a2, s2)

        let a3 = AST.Address.FromR1C1(13,10,"myworkbook","myworksheet","foobar")
        let s3 = "helloworld!"
        job.AddInput(a3, s3)

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
