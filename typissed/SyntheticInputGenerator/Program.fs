open System
open ExcelParser

[<EntryPoint>]
let main argv = 
    if argv.Length <> 6 then
        printfn "Usage: ConsoleApp [AWS key] [AWS secret] [s3 bucket] [output filename] [number of strings] [images per hit]"
        1
    else
        // args
        let aws_key = argv.[0]
        let aws_secret = argv.[1]
        let s3_bucket = argv.[2]
        let outputfile = argv.[3]
        let numstrings = System.Int32.Parse(argv.[4])
        let images_per_hit = System.Int32.Parse(argv.[5])

        TyPissed.Job.CleanBucket(s3_bucket, aws_key, aws_secret);

        printfn "Generating %d random floating point numbers..." numstrings

        // init RNG
        let rng = System.Random()

        // generate a big pile of doubles
        // roughly half will be negative numbers
        let dbls = Array.Parallel.map (fun i -> rng.NextDouble() * 100000000.0 * if rng.Next(2) = 0 then -1.0 else 1.0) [|0..numstrings-1|]

        // convert these to strings
        // some number of the positive ones will have
        // prepended plus signs
        // precision is 12, including signs
        let dstrs = Array.Parallel.map (fun (d: double) ->
                                            match d > 0.0 && rng.Next(2) = 0 with
                                            | true -> "+" + d.ToString("F12").Substring(0,10)
                                            | _ -> d.ToString("F12").Substring(0,11)
                                        ) dbls

        printfn "Creating job..."
        let job = TyPissed.Job(aws_key, s3_bucket, images_per_hit)

        // add to Job
        Array.iteri (fun i dstr -> job.AddInput(TyPissed.Job.SimulatedAddress(i), dstr)) dstrs

        printfn "%s\n" (job.Statistics())

        printfn "Rendering bitmaps and uploading to S3..."
        job.UploadAllImages(aws_secret)

        printfn "Writing to %s" outputfile
        job.WriteJobToCSV(outputfile)

        printfn "Uploading job state to S3..."
        let state = (job.SerializeToS3(aws_secret))
//        let outfile = job.SerializeToFile()
//        printfn "Job state written to: %s" outfile

        printfn "Paste the following into MTurk:\n-------PASTE START-------\n\n%s\n\n-------PASTE END------\n" (job.WriteTurkTemplate())

        0 // return an integer exit code
