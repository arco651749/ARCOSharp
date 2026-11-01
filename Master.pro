[Program 0]
Name="Master_Production"
Type="0"
Size="3"
1="System@Log Message	Production Started"
2="IO@SetPort	P0.0,1,ForceUpdate"
3="System@Delay	100"

[Program 1]
Name="Thread_SubRoutine"
Type="0"
Size="3"
1="System@Log Message	SubRoutine running v2"
2="IO@SetPort	P0.2,1,ForceUpdate"
3="System@Delay	50"
