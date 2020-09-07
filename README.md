# DeedleExtensions

This repository contains additional functions for handling Deedle frames. 

## Align

Especially the align function is a very handy tool, allowing easy and flexible multi key joining of two frames. 
For each frame, a function is given to the function which maps the keys of both frames to shared keys. The two frames are then joined in a way, that all combinations of rows mapping to the same shared key are created.

Let's say we have the two frames

#### frame1
<pre>
                 Hometown   
Frank   Team1 -> Frankfurt  
Barbara Team1 -> Stralsund  
Alice   Team2 -> Wonderland 
Bruce   Team2 -> Cranberra  
</pre>
#### frame2
<pre>
         Room 
Team1 -> 28   
Team2 -> 42
</pre>

We can join them over the shared teamName:

```F#
Frame.align snd id frame1 frame2
|> Frame.mapRowKeys (fun (team,(name,_),_) -> name,team)
```

<pre>
                 Hometown   Room 
Frank   Team1 -> Frankfurt  28   
Barbara Team1 -> Stralsund  28   
Alice   Team2 -> Wonderland 42   
Bruce   Team2 -> Cranberra  42 
</pre>
