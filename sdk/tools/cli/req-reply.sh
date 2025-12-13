# set up one or more replier
 nats reply cloops.test "{{ID}}: How are you? i have sent {{Count}} messages today"

 # send req
 nats req cloops.test "whats up"