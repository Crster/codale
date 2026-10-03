namespace Codale.App.ViewModels;

/// <summary>
/// The bottom status line's patter while the agent works: a waiting clock alone reads
/// as "stuck", a line that changes every few seconds reads as "busy".
/// </summary>
/// <remarks>
/// Drawn from a shuffled bag rather than at random: every phrase comes up once before
/// any comes up again, so with a change every few seconds nothing repeats for well over
/// half an hour - and a fresh shuffle never opens with the line that just closed the last.
/// </remarks>
public static class WorkingPhrases
{
    private static readonly string[] Phrases = new[]
    {
        // The originals.
        "Reticulating splines", "Herding semicolons", "Consulting the rubber duck", "Untangling the spaghetti",
        "Polishing pixels", "Negotiating with the compiler", "Brewing fresh bytes", "Aligning the curly braces",
        "Counting to infinity, twice", "Feeding the hamsters", "Refactoring the universe", "Warming up the flux capacitor",
        "Chasing a wild pointer", "Teaching bits to dance", "Tidying the call stack", "Asking the linter nicely",
        "Defragmenting thoughts", "Converting coffee into code", "Befriending the garbage collector", "Measuring twice, cutting once",
        "Juggling promises", "Untying knots in the thread pool", "Whispering to the regex", "Squashing gremlins",
        "Tuning the quantum harp", "Hunting the missing semicolon", "Shuffling electrons", "Calibrating the vibes",
        "Pondering the orb", "Petting the build server", "Reading the manual (finally)", "Bribing the cache",
        "Folding origami out of ASTs", "Rehearsing the stand-up", "Sweeping up stray tabs", "Summoning the stack trace",
        "Sharpening the pencils", "Dusting off the docs", "Charging the lasers", "Plotting world domination, small scale",
        "Gathering stardust", "Politely poking the API", "Knitting a linked list", "Waiting for the kettle to boil",
        "Counting sheep in binary",

        // Code, lovingly.
        "Renaming variables to something sensible", "Deleting code with great joy", "Adding one more abstraction layer",
        "Removing one abstraction layer", "Resolving merge conflicts with diplomacy", "Writing a strongly worded comment",
        "Un-commenting the commented-out code", "Inventing a better name for 'data2'", "Checking if it's a caching problem",
        "Blaming DNS, just in case", "Turning it off and on again", "Adding a TODO for future me", "Honouring the TODO from past me",
        "Reversing a linked list for fun", "Balancing the binary tree", "Sorting things, stably", "Hashing out the details",
        "Allocating some patience", "Freeing unused worries", "Dereferencing good intentions", "Casting spells, then types",
        "Boxing and unboxing the values", "Awaiting the awaitable", "Yielding gracefully", "Catching exceptions mid-air",
        "Throwing only the good exceptions", "Handling the unhandled", "Wrapping it in a try block", "Finally reaching finally",
        "Escaping the escape characters", "Quoting the quotes", "Trimming trailing whitespace", "Indenting with conviction",
        "Debating tabs versus spaces", "Choosing spaces, diplomatically", "Counting the brackets again", "Closing every open paren",
        "Linting the lint", "Formatting the formatter's output", "Compiling a list of compilers", "Linking the linker",
        "Optimising the premature optimisation", "Profiling the profiler", "Benchmarking a coffee break", "Memoising good ideas",
        "Caching the cache key", "Invalidating the right cache", "Naming things, the hardest part", "Off-by-one-ing carefully",
        "Fencing the fencepost error", "Rounding towards sanity", "Parsing the unparseable", "Tokenising the tokens",
        "Lexing with flair", "Walking the syntax tree", "Pruning the syntax tree", "Grafting a new branch",
        "Rebasing onto reality", "Cherry-picking the good bits", "Squashing tiny commits", "Amending history, gently",
        "Stashing the chaos", "Popping the stash", "Bisecting the mystery", "Blaming git blame",
        "Reading the diff twice", "Rewriting the commit message", "Pushing, but not force-pushing", "Pulling the latest and greatest",
        "Fetching the upstream gossip", "Tagging a release in spirit", "Branching out", "Merging with confidence",
        "Mocking the mocks", "Stubbing a toe on a stub", "Faking it until it passes", "Asserting the obvious",
        "Testing the tests", "Making the red go green", "Refactoring while green", "Writing a test that fails first",
        "Covering the uncovered lines", "Fuzzing the fuzzy bits", "Snapshotting the snapshot", "Flaking out on flaky tests",
        "Retrying, deterministically", "Seeding the random generator", "Randomising responsibly", "Generating a unique ID, uniquely",
        "Serialising the serial", "Deserialising the cereal", "Marshalling the marshals", "Encoding in UTF-8, obviously",
        "Decoding the base64 mystery", "Escaping JSON with care", "Validating the schema", "Normalising the database",
        "Denormalising for speed", "Indexing the indexes", "Joining tables, politely", "Selecting star, then regretting",
        "Migrating the migrations", "Rolling back the rollback", "Committing the transaction", "Locking the right rows",
        "Paginating the endless list", "Throttling the eager requests", "Debouncing the bouncy input", "Queueing up the queue",
        "Dequeuing with gusto", "Popping the stack", "Pushing onto the heap", "Sweeping the heap",

        // The machine room.
        "Warming up the CPU", "Cooling down the GPU", "Spinning up the fans", "Defrosting the RAM",
        "Dusting the motherboard", "Rebooting the imagination", "Updating the drivers of change", "Plugging in the extension cord",
        "Checking the cables twice", "Reseating the RAM, spiritually", "Overclocking the enthusiasm", "Undervolting the stress",
        "Paging in the good memories", "Swapping out the bad ones", "Flushing the buffers", "Filling the pipeline",
        "Predicting the branch", "Speculatively executing", "Retiring instructions honourably", "Fetching, decoding, executing",
        "Registering the registers", "Interrupting the interrupts", "Scheduling the scheduler", "Context-switching, briefly",
        "Forking a process", "Spooning a process", "Joining the threads", "Mutexing the mutable",
        "Semaphoring politely", "Avoiding the deadlock", "Dodging race conditions", "Winning the race condition",
        "Pinging localhost", "Resolving localhost to home", "Opening a socket", "Closing the socket drawer",
        "Handshaking, firmly", "Negotiating TLS", "Renewing the certificates", "Routing the packets",
        "Reassembling the packets", "Retransmitting the lost ones", "Tuning the TCP window", "Pinging the ping",
        "Traceroute-ing the scenic path", "Load balancing the load", "Scaling horizontally", "Scaling vertically",
        "Containerising the container", "Orchestrating the orchestra", "Composing the docker compose", "Shipping the container ship",
        "Deploying to staging, bravely", "Not deploying on Friday", "Rolling out gradually", "Canarying the canary",
        "Feature-flagging the flags", "Monitoring the monitors", "Alerting the alerts", "Graphing the graphs",
        "Logging the logs", "Rotating the logs", "Grepping the logs", "Tail-ing the tail",
        "Checking the uptime", "Counting the nines", "Keeping the lights on", "Paging nobody, thankfully",

        // Kitchen.
        "Preheating the oven", "Kneading the dough", "Proofing the bread", "Folding in the egg whites",
        "Simmering the sauce", "Reducing the reduction", "Deglazing the pan", "Seasoning to taste",
        "Tasting the soup", "Adding a pinch of salt", "Grinding fresh pepper", "Zesting a lemon",
        "Whisking vigorously", "Chopping the onions, bravely", "Caramelising the onions", "Toasting the spices",
        "Blooming the saffron", "Tempering the chocolate", "Frosting the cupcakes", "Decorating with sprinkles",
        "Steeping the tea", "Frothing the milk", "Pulling an espresso shot", "Grinding the coffee beans",
        "Brewing a pour-over", "Buttering the toast", "Flipping the pancakes", "Poaching the eggs",
        "Rolling the sushi", "Folding the dumplings", "Stretching the noodles", "Stirring the risotto",
        "Plating it beautifully", "Garnishing with parsley", "Letting the dough rest", "Setting the table",
        "Slow-roasting the ideas", "Marinating the thoughts", "Fermenting a good plan", "Pickling the edge cases",

        // Garden and nature.
        "Watering the plants", "Pruning the bonsai", "Planting a seed of an idea", "Raking the leaves",
        "Weeding the garden", "Composting old code", "Mulching the flower beds", "Repotting the cactus",
        "Talking to the tomatoes", "Chasing away the squirrels", "Counting the petals", "Harvesting the pumpkins",
        "Following the bees", "Listening to the rain", "Watching the clouds drift", "Skipping stones",
        "Climbing the tallest tree", "Building a sandcastle", "Collecting seashells", "Chasing the sunset",
        "Stargazing for a moment", "Naming the constellations", "Waiting for the tide", "Hiking up the hill",
        "Crossing the river on stones", "Following the trail markers", "Pitching the tent", "Lighting the campfire",
        "Toasting marshmallows", "Telling campfire stories", "Spotting a shooting star", "Catching fireflies",

        // Animals.
        "Herding the cats", "Walking the dog", "Feeding the goldfish", "Teaching the parrot to code",
        "Negotiating with a goose", "Waking the sleepy owl", "Following the white rabbit", "Counting the ducks in a row",
        "Lining up the ducks", "Calming the startled deer", "Befriending a raccoon", "Racing a snail, and losing",
        "Petting the office cat", "Giving the cat a keyboard break", "Untangling the kitten's yarn", "Waking up the hedgehog",
        "Consulting the wise tortoise", "Asking the octopus for a hand", "Sharing snacks with a panda", "Borrowing a penguin's tuxedo",
        "Teaching an old dog new tricks", "Letting the sleeping dogs lie", "Tickling the dragon, carefully", "Grooming the llama",
        "Brushing the alpaca", "Feeding the carrier pigeons", "Training the homing pigeons", "Listening to the whales sing",
        "Hatching the eggs", "Counting chickens after they hatch", "Watching the ant colony", "Following the ant trail",

        // Space and science.
        "Launching the rocket", "Counting down from ten", "Calculating the orbit", "Adjusting the trajectory",
        "Docking with the space station", "Deploying the solar panels", "Mapping the dark side of the moon", "Measuring the speed of light",
        "Splitting hairs, not atoms", "Mixing the potion", "Titrating carefully", "Balancing the equation",
        "Consulting the periodic table", "Growing the crystals", "Calibrating the telescope", "Polishing the lenses",
        "Observing without disturbing", "Collapsing the wave function", "Entangling some particles", "Tunnelling through the barrier",
        "Bending spacetime slightly", "Folding a paper wormhole", "Reversing the polarity", "Recalibrating the deflector dish",
        "Charging the warp drive", "Plotting a course to the stars", "Engaging at warp two", "Scanning for life forms",
        "Decoding the alien signal", "Waving at the satellites", "Tracking the comet", "Naming a new asteroid",

        // Magic and fantasy.
        "Casting a helpful spell", "Brewing a debugging potion", "Consulting the ancient scrolls", "Reading the runes",
        "Polishing the crystal ball", "Shuffling the tarot cards", "Summoning a friendly daemon", "Banishing the gremlins",
        "Enchanting the variables", "Charming the compiler", "Waving the magic wand", "Sweeping with the magic broom",
        "Feeding the familiar", "Sharpening the sword of refactoring", "Forging a new helper", "Tempering the steel",
        "Questing for the lost bug", "Slaying the tiny dragon", "Rescuing the null reference", "Unlocking the dungeon door",
        "Rolling for initiative", "Rolling a natural twenty", "Checking the treasure map", "Following the breadcrumbs",
        "Decoding the prophecy", "Asking the oracle", "Consulting the wizard", "Riding the unicorn to prod",
        "Taming the hydra of dependencies", "Solving the sphinx's riddle", "Crossing the troll's bridge", "Lighting the beacons",

        // Music and art.
        "Tuning the guitar", "Rosining the bow", "Warming up the vocal cords", "Practising the scales",
        "Composing a small symphony", "Keeping the tempo", "Dropping the beat", "Remixing the logic",
        "Harmonising the functions", "Conducting the orchestra of threads", "Finding the right chord", "Playing it by ear",
        "Mixing the colours", "Stretching the canvas", "Sketching a rough draft", "Inking the outlines",
        "Adding the finishing touches", "Framing the masterpiece", "Sculpting the clay", "Glazing the pottery",
        "Weaving the tapestry", "Stitching it together", "Knitting a cosy function", "Crocheting a callback",
        "Arranging the flowers", "Choreographing the pipeline", "Rehearsing the grand finale", "Taking a bow",

        // Everyday life.
        "Making the bed", "Folding the laundry", "Matching the socks", "Finding the missing sock",
        "Organising the drawers", "Labelling everything", "Alphabetising the spice rack", "Watering the office plant",
        "Refilling the stapler", "Untangling the headphones", "Charging the phone", "Finding the remote",
        "Looking for the car keys", "Checking behind the couch", "Emptying the dishwasher", "Taking out the recycling",
        "Sorting the mail", "Paying the parking meter", "Feeding the parking meter again", "Catching the bus",
        "Waiting for the green light", "Merging into traffic", "Parallel parking, carefully", "Asking for directions",
        "Reading the fine print", "Signing on the dotted line", "Filing the paperwork", "Stamping the forms",
        "Standing in line", "Taking a number", "Ringing the bell", "Knocking politely",
        "Opening the window for fresh air", "Adjusting the thermostat", "Fluffing the pillows", "Lighting a scented candle",

        // Sports and games.
        "Stretching before the sprint", "Lacing up the sneakers", "Warming up on the sidelines", "Running a few laps",
        "Doing a quick push-up", "Practising the free throws", "Lining up the putt", "Reading the green",
        "Serving an ace", "Returning the volley", "Dribbling past defenders", "Scoring from midfield",
        "Calling the play", "Huddling up", "Diving for the catch", "Stealing second base",
        "Solving the crossword", "Placing the last puzzle piece", "Turning the Rubik's cube", "Aligning the Tetris blocks",
        "Clearing four lines at once", "Collecting the power-up", "Finding the secret level", "Saving at the checkpoint",
        "Grinding for experience", "Levelling up", "Respawning at the base", "Beating the boss on the first try",
        "Castling kingside", "Sacrificing a pawn wisely", "Thinking three moves ahead", "Calling checkmate, eventually",

        // Books and learning.
        "Turning the page", "Dog-earing the good part", "Taking notes in the margin", "Highlighting the key idea",
        "Looking it up in the dictionary", "Checking the index", "Reading the footnotes", "Cross-referencing the appendix",
        "Borrowing a book from the library", "Returning it on time", "Shushing the noisy thoughts", "Finding the right shelf",
        "Studying for the pop quiz", "Doing the homework", "Showing the work", "Double-checking the maths",
        "Carrying the one", "Long-dividing patiently", "Drawing a helpful diagram", "Explaining it to a rubber duck",
        "Drawing boxes and arrows", "Filling the whiteboard", "Erasing the whiteboard", "Finding a marker that works",

        // Travel.
        "Packing the suitcase", "Checking the passport", "Printing the boarding pass", "Finding the gate",
        "Boarding in zone three", "Stowing the carry-on", "Fastening the seatbelt", "Taxiing to the runway",
        "Cruising at altitude", "Enjoying the in-flight peanuts", "Crossing the date line", "Adjusting to the time zone",
        "Hailing a taxi", "Reading the metro map", "Transferring at the next stop", "Validating the ticket",
        "Asking a local for tips", "Trying the street food", "Sending a postcard", "Taking the scenic route",
        "Getting delightfully lost", "Finding the way back", "Unpacking the souvenirs", "Sorting the holiday photos",

        // Pure nonsense.
        "Inflating the rubber ducks", "Counting the grains of sand", "Measuring a piece of string", "Teaching a rock to think",
        "Rearranging the deck chairs", "Reinventing a rounder wheel", "Putting the cart after the horse", "Nailing jelly to the wall",
        "Herding digital sheep", "Painting the bikeshed", "Choosing the bikeshed's colour", "Repainting the bikeshed",
        "Sprinkling some magic dust", "Adding extra sparkle", "Polishing the chrome", "Buffing out the scratches",
        "Bubble-wrapping the edge cases", "Wrapping presents for the tests", "Tying a neat bow on it", "Popping the bubble wrap",
        "Stacking the pancakes of logic", "Balancing spoons on noses", "Juggling flaming torches", "Spinning the plates",
        "Walking the tightrope", "Riding a unicycle, slowly", "Pulling a rabbit out of a hat", "Sawing the assistant in half, gently",
        "Finding the end of the rainbow", "Following the yellow brick road", "Clicking the ruby slippers", "Opening the wardrobe to Narnia",
        "Asking the magic eight ball", "Reply hazy, trying again", "Consulting the fortune cookie", "Reading the tea leaves",
        "Blowing out the candles", "Making a wish", "Tossing a coin in the fountain", "Crossing fingers and toes",
        "Knocking on wood", "Avoiding the ladders", "Keeping the black cats happy", "Carrying a lucky penny",
        "Humming the elevator music", "Pressing the elevator button twice", "Holding the door", "Waving to the security camera",
        "Doodling in the margins", "Drawing a tiny moustache", "Folding a paper aeroplane", "Launching the paper aeroplane",
        "Blowing soap bubbles", "Chasing the bubbles", "Building a blanket fort", "Defending the blanket fort",
        "Hiding from the bugs", "Seeking the bugs", "Playing hide and seek with a null", "Tagging the bug, you're it",
        "Shaking the snow globe", "Winding the music box", "Turning the kaleidoscope", "Spinning the fidget spinner",
        "Popping popcorn", "Sharing the popcorn", "Saving you a seat", "Dimming the lights for the show",
        "Taking a deep breath", "Counting to ten, calmly", "Finding inner peace", "Centring the div",
        "Centring the div, vertically too", "Reading the room", "Thinking outside the box", "Thinking inside a smaller box",
        "Putting on the thinking cap", "Adjusting the thinking cap", "Scratching the head", "Stroking an imaginary beard",
        "Staring thoughtfully into the distance", "Having a eureka moment", "Having a second eureka moment", "Writing it on a napkin",
        "Connecting the dots", "Colouring inside the lines", "Joining the dots into a giraffe", "Seeing the big picture",
        "Zooming in on the details", "Zooming back out again", "Squinting at the tiny print", "Cleaning the glasses",
        "Getting to the bottom of it", "Peeling back the layers", "Following the thread", "Pulling on the loose string",
        "Weighing the options", "Flipping a mental coin", "Sleeping on it, very briefly", "Waking up with the answer",
        "Almost there, probably", "Nearly done, most likely", "Just one more thing", "Wrapping things up, soon-ish",
    };

    // The bag is shared by every chat tab, so it only ever runs on the UI thread.
    private static readonly List<string> Bag = [];
    private static string? _last;

    public static string Next(string? previous)
    {
        if (Bag.Count == 0)
        {
            Refill(previous ?? _last);
        }

        var next = Bag[^1];
        Bag.RemoveAt(Bag.Count - 1);
        _last = next;
        return next + "…";
    }

    private static void Refill(string? justShown)
    {
        var phrases = Phrases.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Random.Shared.Shuffle(phrases);
        Bag.AddRange(phrases);

        // The bag is drawn from its end: keep the phrase just shown from opening the new round.
        var trimmed = justShown?.TrimEnd('…');
        if (Bag.Count > 1 && string.Equals(Bag[^1], trimmed, StringComparison.OrdinalIgnoreCase))
        {
            (Bag[0], Bag[^1]) = (Bag[^1], Bag[0]);
        }
    }
}
