	.section .itcm,4,1,4

	.global FastCopy
	.type FastCopy, @function
	.arm
FastCopy:
	ldr r2, [r1], #4
	str r2, [r0], #4
	bx lr
	.size FastCopy, .-FastCopy
