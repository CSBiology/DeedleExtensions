﻿namespace DeedleExtensions

open Deedle
open Deedle.Vectors
open Deedle.Indices
open Deedle.Indices.Linear

module internal Helpers =

    open System

    /// A generic transformation that works when at most one value is defined
    let atMostOne = 
        { new IBinaryTransform with
            member vt.GetFunction<'R>() = (fun (l:OptionalValue<'R>) (r:OptionalValue<'R>) -> 
              if l.HasValue && r.HasValue then invalidOp "Combining vectors failed - both vectors have a value."
              if l.HasValue then l else r)
            member vt.IsMissingUnit = true }
        |> VectorListTransform.Binary

    let transformColumn (vectorBuilder:IVectorBuilder) scheme rowCmd (vector:IVector) = 
      { new VectorCallSite<IVector> with
          override x.Invoke<'T>(col:IVector<'T>) = 
            vectorBuilder.Build<'T>(scheme, rowCmd, [| col |]) :> IVector }
      |> vector.Invoke

    /// Reorder elements in the index to match with another index ordering after applying the given function to the first index
    let reindexBy (keyFunc : 'RowKey1 -> 'RowKey2) (index1:IIndex<'RowKey1>) (index2:IIndex<'RowKey2>) vector = 
        let relocations = 
                seq {  
                    for KeyValue(key, newAddress) in index1.Mappings do
                    let key = keyFunc key
                    let oldAddress = index2.Locate(key)
                    if oldAddress <> Addressing.Address.invalid then 
                        yield newAddress, oldAddress }
        Vectors.Relocate(vector, index1.KeyCount, relocations)

    ///Returns all possible combinations of second and first frame keys sharing the same keyfunction result
    let combineIndexBy (keyFunc1 : 'RowKey1 -> 'SharedKey) (keyFunc2 : 'RowKey2 -> 'SharedKey) (index1:IIndex<'RowKey1>) (index2:IIndex<'RowKey2>) =
        // Group by the shared key
        let g1 = index1.Keys |> Seq.groupBy keyFunc1
        let g2 = index2.Keys |> Seq.groupBy keyFunc2
    
        //Intersect over the shared Keys
        let s1,s2 = g1 |> Seq.map fst |> Set.ofSeq, g2 |> Seq.map fst |> Set.ofSeq    
        let keyIntersect =  Set.intersect s1 s2 

        //For each shared key, create all possible combinations of index1 and index2 keys
        let m1,m2 = g1 |> Map.ofSeq,g2 |> Map.ofSeq
        let newKeys = 
            keyIntersect
            |> Seq.collect (fun key -> 
                    Map.find key m1
                    |> Seq.collect(fun k1 -> 
                        Map.find key m2
                        |> Seq.map (fun k2 -> 
                            key,k1,k2)
                        )
                )

        //Return new index
        LinearIndexBuilder.Instance.Create(newKeys,None)

    /// Create transformation on indices/vectors representing the align operation
    let createAlignTransformation (keyFunc1 : 'RowKey1 -> 'SharedKey) (keyFunc2 : 'RowKey2 -> 'SharedKey) (thisIndex:IIndex<_>) (otherIndex:IIndex<_>) thisVector otherVector =
        let combinedIndex = combineIndexBy keyFunc1 keyFunc2 thisIndex otherIndex
        let rowCmd1 = reindexBy (fun (a,b,c) -> b) combinedIndex thisIndex thisVector
        let rowCmd2 = reindexBy (fun (a,b,c) -> c) combinedIndex otherIndex otherVector
        combinedIndex,rowCmd1,rowCmd2

    /// Create transformation on indices/vectors representing the expand operation
    let createExpandTransformation (expandF : 'R -> 'RS seq) (index : IIndex<'R>) (vector : VectorConstruction) = 
        ///new Keys * old Keys 
        let newKeys = 
            index.Keys
            |> Seq.collect (fun r -> expandF r |> Seq.map (fun rs -> rs,r))
        let keyFunc = 
            let m = newKeys |> Map.ofSeq
            fun rs -> m.[rs]
        let newIndex = LinearIndexBuilder.Instance.Create(newKeys |> Seq.map fst,None)
        let rowCmd = reindexBy keyFunc newIndex index vector
        newIndex,rowCmd

    /// If the input is non empty, returns `Some(head)` where `head` is
    /// the first value. Otherwise, returns `None`.
    let headOrNone (input:seq<_>) =
      System.Linq.Enumerable.FirstOrDefault(input |> Seq.map Some)

    //type IBoxedVector =
    //      inherit IVector<obj>
    //      abstract UnboxedVector : IVector

    //let inline unboxVector (v:IVector) =
    //    match v with
    //    | :? IBoxedVector as vec -> vec.UnboxedVector
    //    | vec -> vec

    //let fromColumnsNonGeneric indexBuilder vectorBuilder (seriesConv:'S -> ISeries<_>) (nested:Series<_, 'S>) =
    //    let columns = Series.observations nested |> Array.ofSeq
    //    let rowIndex = columns |> Seq.head |> (fun (_, s) -> (seriesConv s).Index)
    //    // OPTIMIZATION: If all series have the same index (same object), then no join is needed
    //    // (This is particularly valuable for things like +, *, /, - operators on Frame)
    //    let vector = columns |> Seq.map (fun (_, s) ->
    //        // When the nested series data is in 'IBoxedVector', get the unboxed representation
    //        unboxVector (seriesConv s).Vector) |> Vector.ofValues
    //    Frame<_, _>(rowIndex, Index.ofKeys (Array.map fst columns), vector, indexBuilder, vectorBuilder)
   

module Series = 

    /// Appends a given series by a key it's value 
    let append (s:Series<'key,'value>) key value =
        s
        |> Series.observations
        |> Seq.append [(key,value)]
        |> Series.ofObservations

module Frame =
    
    open Helpers

    /// Removes column from frame if it exists
    let dropColIfExists (col:'C) (frame:Frame<'R,'C>)  =
        try 
            frame |> Frame.dropCol col
        with _ ->
            frame

    /// Removes multiple columns from frame if they exist
    let dropColsIfExist (cols:seq<'C>) (frame:Frame<'R,'C>)  =
        let rec loop (cols:'C list) f =
            match cols with
            |col::tail -> loop tail (f |> dropColIfExists col)
            |_ -> f
        loop (cols |> Seq.toList) frame

    /// For given frame, drops all rows except the first appearing of distinct column value
    let distinctRowValues colName (df:Frame<int,_>) = 
        df
        |> Frame.groupRowsByString colName
        |> Frame.applyLevel fst (fun os -> os.FirstValue())
        |> Frame.indexRowsOrdinally

    /// Compose they keys and values specified by the column name and apply the function f to all values of the same key
    let composeRowsBy (f : 'b seq -> 'c) (keyColName:'a) (valueColName:'a) (table:Frame<_,'a>) =
        let keys =
            table.GetColumn<'d>(keyColName)
            |> Series.values
        let values = 
            table.GetColumn<'b>(valueColName)
            |> Series.values

        Seq.zip keys values  
        |> Seq.groupBy fst
        |> Seq.map (fun (a,b) -> a, b |> Seq.map snd |> f)
        |> Series.ofObservations

    ///// Use expandRowsbyColumn instead ///////
    //let decomposeRowsBy f colName (df:Frame<int,_>) =
    //    df
    //    |> Frame.groupRowsByString colName
    //    |> Frame.rows
    //    |> Series.observations
    //    |> Seq.collect (fun (k,os) -> f k
    //                                  |> Seq.mapi (fun i v -> let k',i' = k
    //                                                          (k',i'+ i),Series.append os "decomposed" (v))) // * (box v)
    //    |> Frame.ofRows
    //    |> Frame.indexRowsOrdinally

 
    /// Creates a frame from a column array and the according keys. Length of outer array has to match colNames length. Length of inner array has to match rowNames length.
    let ofJaggedArrayCol (rowNames:'rKey seq) (colNames:'cKey seq) (colJarray:'value array array) =   
        
        let colNamesLength = colNames |> Seq.length
        let rowNamesLength = rowNames |> Seq.length
        if colJarray.Length <> colNamesLength then 
            failwithf "Creaing frame of column arrays failed: length %i of column names does not match length %i of column array" colNamesLength colJarray.Length
        let mutable i = 0

        colJarray
        |> Seq.map2 (fun colKey arr -> 

            if arr.Length <> rowNamesLength then
                failwithf "Creaing frame of column arrays failed: length %i of row names does not match length %i of column at position %i" rowNamesLength arr.Length i
            i <- i + 1

            colKey,Series(rowNames, arr)  )            
            colNames 
        |> frame 


    /// Align two data frames by a shared key received through mapping functions. 
    ///
    /// The columns of the joined frames must not overlap and their rows are aligned and multiplied
    /// by the shared key. The keyFuncs are used to map the rowKeys of the two frames to a shared key. 
    /// The resulting keys will result from the intersection of the shared Keys.
    ///
    /// The key of the resulting frame will be a triplet of shared key and the two input keys.
    let align (keyFunc1 : 'RowKey1 -> 'SharedKey) (keyFunc2 : 'RowKey2 -> 'SharedKey) (frame1 : Frame<'RowKey1, 'TColumnKey>) (frame2 : Frame<'RowKey2, 'TColumnKey>) =  
        //Get needed transformation objects and data form the Frame
        let index1 = frame1.RowIndex
        let index2 = frame2.RowIndex
        let indexBuilder = LinearIndexBuilder.Instance
        let vectorbuilder = ArrayVector.ArrayVectorBuilder.Instance
        let data1 = frame1.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues
        let data2 = frame2.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues
        // Intersect mapped row indices and get transformations to apply to input vectors
        let newRowIndex, rowCmd1, rowCmd2 = 
          createAlignTransformation keyFunc1 keyFunc2 index1 index2 (Vectors.Return 0) (Vectors.Return 0)
        // Append the column indices and get transformation to combine them
        let newColumnIndex, colCmd = 
            indexBuilder.Merge( [(frame1.ColumnIndex, Vectors.Return 0); (frame2.ColumnIndex, Vectors.Return 1) ], atMostOne, false)
        // Apply transformation to both data vectors
        let newData1 = data1.Select(transformColumn vectorbuilder newRowIndex.AddressingScheme rowCmd1)
        let newData2 = data2.Select(transformColumn vectorbuilder newRowIndex.AddressingScheme rowCmd2)
        // Combine column vectors a single vector & return results
        let newData = vectorbuilder.Build(newColumnIndex.AddressingScheme, colCmd, [| newData1; newData2 |])
        Frame(newRowIndex, newColumnIndex, newData, indexBuilder, vectorbuilder)
    
    /// Applies the function f to the rowKeys of the frame and adds the result as a new column to the frame
    let columnOfRowKeysBy (columnKey : 'C) (f : 'R -> 'T) (frame : Frame<'R,'C>) : Frame<'R,'C> =
        let newColumn = 
            frame.RowKeys
            |> Seq.map f
            |> Seq.zip frame.RowKeys
            |> series
        Frame.addCol columnKey newColumn frame

    /// Adds the rowKeys as a new column to the frame
    let columnOfRowKeys (columnKey : 'C) (frame : Frame<'R,'C>) : Frame<'R,'C> =
        columnOfRowKeysBy columnKey id frame

    /// Applies the function f to the values of given column of the frame and adds the result as a new column to the frame
    let columnOfColumnBy (oldColumnKey : 'C) (newColumnKey : 'C)  (f : 'T -> 'U) (frame : Frame<'R,'C>) : Frame<'R,'C> =
        let newColumn = 
            frame.GetColumn<'T> oldColumnKey
            |> Series.mapValues f
        Frame.addCol newColumnKey newColumn frame

    /// Applies the function f to the values of given column of the frame and adds the result as a new column to the frame
    let columnOfColumnsBy (oldColumnKeys : 'C seq) (newColumnKey : 'C)  (f : 'R -> 'T seq -> 'U) (frame : Frame<'R,'C>) : Frame<'R,'C> =
        let newColumn = 
            Frame.sliceCols oldColumnKeys frame
            |> Frame.mapRows (fun r os -> os.As<'T>().Values |> f r)
        Frame.addCol newColumnKey newColumn frame

    ///// If the predicate returns false for a value, replaces the value with missing
    //let filter (predicate : 'R -> 'C -> 'a -> bool) (frame : Frame<'R,'C>) = // : Frame<'R,'C> =
    //    let indexBuilder = LinearIndexBuilder.Instance
    //    let vectorBuilder = ArrayVector.ArrayVectorBuilder.Instance
    //    let f = System.Func<_,_,_,_>(predicate)
    //    frame.Columns |> Series.map (fun c os ->
    //        match os.TryAs<'a>(ConversionKind.Safe) with
    //        | OptionalValue.Present s -> Series.filter (fun r v -> f.Invoke(r, c, v)) s :> ISeries<_>
    //        | _ -> os :> ISeries<_>)
    //    |> fromColumnsNonGeneric indexBuilder vectorBuilder id

    ///// If the predicate returns false for a value, replaces the value with missing
    //let filterValues (predicate : 'a -> bool) (frame : Frame<'R,'C>) : Frame<'R,'C> =
    //    let indexBuilder = LinearIndexBuilder.Instance
    //    let vectorBuilder = ArrayVector.ArrayVectorBuilder.Instance
    //    let f = System.Func<_,_>(predicate)
    //    frame.Columns |> Series.mapValues (fun os ->
    //        match os.TryAs<'a>(ConversionKind.Safe) with
    //        | OptionalValue.Present s -> Series.filterValues (fun v -> f.Invoke(v)) s :> ISeries<_>
    //        | _ -> os :> ISeries<_>)
    //    |> fromColumnsNonGeneric indexBuilder vectorBuilder id

    /// Creates a new data frame that contains only those columns of the original 
    /// data frame which contain at least one value.
    let dropEmptyRows (frame:Frame<'R, 'C>) = 
        //Get needed transformation objects and data form the Frame
        let indexBuilder =  LinearIndexBuilder.Instance
        let vectorbuilder = ArrayVector.ArrayVectorBuilder.Instance
        let data = frame.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues

        // Create a combined vector that has 'true' for rows which have some values
        let hasSomeFlagVector = 
            Frame.rows frame
            |> Series.map (fun _ s -> Series.valuesAll s |> Seq.exists (fun opt -> opt.IsSome))
            |> Series.values
            |> ``F# Vector extensions``.Vector.ofValues

        // Collect all rows that have at least some values
        let newRowIndex, cmd = 
            indexBuilder.Search( (frame.RowIndex, Vectors.Return 0), hasSomeFlagVector, true)
        let newData = data.Select(transformColumn vectorbuilder newRowIndex.AddressingScheme cmd)
        Frame<_, _>(newRowIndex, frame.ColumnIndex, newData, indexBuilder, vectorbuilder)        

    /// Creates a new data frame that contains only those columns of the original 
    /// data frame which contain at least one value.
    let dropEmptyCols (frame:Frame<'R, 'C>) = 
        //Get needed transformation objects and data form the Frame
        let indexBuilder =  LinearIndexBuilder.Instance
        let vectorbuilder = ArrayVector.ArrayVectorBuilder.Instance
        let data = frame.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues

        let newColKeys, newData =
            [| for KeyValue(colKey, addr) in frame.ColumnIndex.Mappings do
                match data.GetValue(addr) with
                | OptionalValue.Present(vec) when vec.ObjectSequence |> Seq.exists (fun opt -> opt.HasValue) ->
                    yield colKey, vec :> IVector
                | _ -> () |] |> Array.unzip
        let colIndex = indexBuilder.Create(Deedle.Internal.ReadOnlyCollection.ofArray newColKeys, None)
        Frame(frame.RowIndex, colIndex, vectorbuilder.Create(newData), indexBuilder, vectorbuilder )
        
    /// Creates a Frame where each row is mapped to multiple rows based on the input function.
    let expandRowsByKey (expandF : 'R -> 'RS seq) (frame : Frame<'R,'C>) : Frame<'RS,'C> =
        //Get needed transformation objects and data form the Frame
        let index = frame.RowIndex
        let indexBuilder = LinearIndexBuilder.Instance
        let vectorbuilder = ArrayVector.ArrayVectorBuilder.Instance
        let data = frame.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues
        //expand rows via collection of function results
        let newRowIndex, rowCmd = 
          createExpandTransformation expandF index (Vectors.Return 0)

        // Apply transformation to both data vectors
        let newData = data.Select(transformColumn vectorbuilder newRowIndex.AddressingScheme rowCmd)
        // Combine column vectors a single vector & return results
        Frame(newRowIndex, frame.ColumnIndex, newData, indexBuilder, vectorbuilder)
 
    /// Creates a Frame where each row is mapped to multiple rows based on the input function. The input function takes the rowkey and the value of the given column at this rowkey and returns new keys.
    let expandRowsByColumn (column : 'C) (expandF : 'R -> 'V -> 'RS seq) (frame : Frame<'R,'C>) : Frame<'RS,'C> =
        //Get needed transformation objects and data form the Frame
        let index = frame.RowIndex
        let indexBuilder = LinearIndexBuilder.Instance
        let vectorbuilder = ArrayVector.ArrayVectorBuilder.Instance
        let data = frame.GetFrameData().Columns |> Seq.map snd |> ``F# Vector extensions``.Vector.ofValues
        /// The column over which the rows are expanded
        let column = frame.GetColumn column
        let expandF r = expandF r (column.Get r)
               
        //expand rows via collection of function results
        let newRowIndex, rowCmd = 
          createExpandTransformation expandF index (Vectors.Return 0)

        // Apply transformation to both data vectors
        let newData = data.Select(transformColumn vectorbuilder newRowIndex.AddressingScheme rowCmd)
        // Combine column vectors a single vector & return results
        Frame(newRowIndex, frame.ColumnIndex, newData, indexBuilder, vectorbuilder)

    /// Creates a new data frame, where for each group of rows as specified by `levelSel`, the row which has the lowest resulting value in the given column after applying `op` gets selected.
    let selectRowsByColumn column (levelSel : 'R -> 'NewR) op (frame:Frame<'R,'C>) =
        let operation frame =
            frame
            |> Frame.sortRowsBy column op
            |> fun f -> f.GetRowAt 0
        frame
        |> Frame.pivotTable 
            (fun (rowKey) _-> 
                levelSel rowKey)
            (fun _ _-> "Reduced") 
            operation 
        |> fun f -> f.GetColumn<Series<'C,_>> "Reduced"
        |> Frame.ofRows