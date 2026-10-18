"""Wallpaper concepts for panorama.py: style prefix, one beat per screen, club-logo references, seam prompts.

logos: beat index -> image in ComfyUI/input (white line drawings of the club logo: the "BiBaBu" lettering, the Bart,
the Bier mug and the Busen). The beat must say what to take from the reference ("... exactly like the reference").
"""
TEXT, BART, BIER, BUSEN = "bibabu_text.png", "bibabu_bart.png", "bibabu_bier.png", "bibabu_busen.png"
REF = "exactly like the reference line drawing"
TEXT_REF = "the handwritten lettering \"BiBaBu\" exactly as in the reference logo image"

CONCEPTS = {
    "lan_party": dict(
        style="Seamless segment of an ultra-wide 16-bit pixel art panorama of a huge LAN party in a school gym hall at "
              "night, flat side view straight from the front like a side-scrolling game level, no vanishing point. Upper "
              "third: the gym back wall with high windows, colourful banners and string lights. Middle: one long continuous "
              "row of tables at exactly the same height across the whole image, gamers with headsets sitting side by side "
              "on office chairs, seen from behind, each glowing monitor shows a different colourful video game. Bottom "
              "fifth: the wooden gym floor with tangled network cables, PC towers, backpacks, pizza boxes and energy drink "
              "cans. Every person is the same small size, about one fifth of the image height. Crisp pixels, rich tiny details everywhere, warm and cozy colours, many different people.",
        beats=[
            "At the hall entrance a registration desk under a big cloth banner that shows " + TEXT_REF + "; a queue of "
            "people carrying PC towers and monitors.",
            "A team of gamers in a heated shooter match, one of them jumping up from his chair cheering; a cat sleeps on "
            "top of a PC tower.",
            "A huge white banner on the back wall showing the beard drawing " + REF + "; below it several bearded "
            "gamers playing a strategy game.",
            "A bar counter with beer taps, under a big glowing yellow neon sign of a foaming beer mug shaped " + REF +
            "; people toasting with beer mugs, stacks of pizza.",
            "A small stage with a huge projector screen showing a tournament final, a crowd of gamers standing and "
            "cheering, a golden trophy on a table.",
            "Flat front view of the same single long row of tables: here the gamers use old CRT monitors, and two arcade "
            "cabinets stand against the back wall.",
            "Flat front view of the same single long row of tables; at its end a server rack with blinking lights and a "
            "waterfall of blue network cables, a network admin with a laptop and a coffee mug.",
            "A pink neon sign on the back wall shaped like the bra outline " + REF + "; below it gamers asleep on air "
            "mattresses and in sleeping bags under the tables, one still playing.",
            "A case-modding table with glowing water-cooled RGB computers, someone soldering under a desk lamp.",
            "A snack table with a pyramid of energy drink cans, bowls of chips and gummy bears, and a cat sitting in an "
            "empty pizza box.",
        ],
        logos={0: TEXT, 2: BART, 3: BIER, 7: BUSEN},
        seam="More of the same long row of tables with gamers sitting side by side at glowing monitors, the same back "
             "wall and floor, continuing straight across.",
        seam_neg="text, letters, neon signs, logos, stage, trophy, empty floor, blurry, smooth gradients, perspective",
        neg="perspective, vanishing point, blurry, smooth gradients, photo, 3d render, text, watermark",
        pixel=4,
    ),
    "pixel_quest": dict(
        style="Seamless segment of an ultra-wide 16-bit pixel art video game background, flat side view like a classic "
              "platformer level, no vanishing point. Bright blue sky with fluffy pixel clouds in the upper half, a grassy "
              "ground strip with brown earth and stones along the bottom fifth of the image at exactly the same height "
              "everywhere, horizon line at 60 percent of the image height. Crisp pixels, limited colour palette, flat "
              "shading, rich tiny details: little villagers, animals, flowers, birds, coins.",
        beats=[
            "A small castle with flags on a green hill, knights at the gate, a drawbridge over a moat with ducks.",
            "A market with colourful stalls and villagers trading, a treasure chest in the grass, chickens running around.",
            "A pixel inn with a big wooden sign showing " + TEXT_REF + "; adventurers sitting on benches outside.",
            "A windmill and a wooden bridge over a small river with jumping fish, a fisherman cat on the bridge.",
            "A giant stone statue on a pedestal of a bearded dwarf king whose beard is shaped " + REF + "; pilgrims "
            "kneeling in front of it.",
            "A cave entrance in a rocky hill lit by two torches, a dragon peeking out, miners with pickaxes and carts.",
            "A tavern with a big hanging sign of a foaming beer mug shaped " + REF + "; dwarves toasting with beer mugs "
            "on the terrace, barrels stacked beside the door.",
            "Golden coins and question-mark blocks floating above brick platforms, a hero jumping between them.",
            "Two round hot-air balloons side by side in the sky, their outlines shaped " + REF + "; a sky pirate ship "
            "in the distance.",
            "A campfire with two tents, adventurers roasting sausages and a sleeping cat; a signpost that reads "
            "\"BiBaBu 2 km\".",
        ],
        logos={2: TEXT, 4: BART, 6: BIER, 8: BUSEN},
        seam="Pixel art grass, ground, bushes, sky and clouds continuing straight across, the same ground height.",
        seam_neg="text, letters, signs, buildings, castle, statue, balloons, blurry, smooth gradients, perspective",
        neg="perspective, vanishing point, blurry, smooth gradients, photo, 3d render, watermark",
        pixel=4,
    ),
    "cat_world": dict(
        style="Seamless segment of an ultra-wide 16-bit pixel art panorama of a busy world run entirely by cats, flat side "
              "view like a classic side-scrolling game, no vanishing point. Upper half: a sunny blue sky with pixel clouds. "
              "The grassy surface runs at exactly 55 percent of the image height everywhere. Below it, the bottom 40 percent "
              "is a cut-away view of the underground: brown earth with tunnels, roots, stones and little cat burrows. Every "
              "cat is the same small size, about one tenth of the image height, many cats of all colours (orange, black, "
              "white, grey, calico) busy everywhere. Crisp pixels, limited colour palette, flat shading, rich tiny details.",
        beats=[
            "A red farm barn with a big wooden sign showing " + TEXT_REF + "; cats driving a tiny tractor, cats milking "
            "a cow, chickens. Underground: a cat storing carrots in a burrow.",
            "A fishing pier at a small pond, cats with fishing rods, a fish market stall with fish hanging. Underground: "
            "a cat sleeping in a cozy burrow full of cushions.",
            "The entrance of a mine in a rocky hill. Underground: cat miners with headlamps and pickaxes in the tunnels, "
            "mine carts full of shining gems, and a stone statue of a cat king whose beard is shaped " + REF + ".",
            "A cat city street with small shops, a cat café with cats drinking milk at little tables, a bakery selling "
            "fish-shaped bread. Underground: a subway train full of cats.",
            "A cat brewery with copper kettles and barrels, cats in lederhosen toasting, under a big wooden sign of a "
            "foaming beer mug shaped " + REF + ". Underground: a beer cellar with stacked barrels.",
            "A garage LAN party: cats with headsets at glowing computer monitors, pizza boxes and cables. Underground: "
            "a server room with blinking lights run by a cat admin.",
            "A knitting shop with two giant round balls of yarn side by side in front of it, outlined like the bra "
            "drawing " + REF + "; kittens playing with yarn. Underground: an ant-sized cat tailor workshop.",
            "A construction site: cats with yellow hard hats, a crane lifting a giant fish, scaffolding. Underground: "
            "pipes and a cat plumber.",
            "A park with a fountain, cats having a picnic, a cat band playing on a little stage with a \"BiBaBu\" banner. "
            "Underground: a treasure chest full of cat toys.",
            "Sunny rooftops of little houses with cats sleeping on them, a cat flying a kite. Underground: a cozy cat "
            "library with tiny books.",
        ],
        logos={0: TEXT, 2: BART, 4: BIER, 6: BUSEN},
        seam="Pixel art grass surface with sky and clouds above and the cut-away underground with tunnels and earth "
             "below, a few cats, continuing straight across at the same heights.",
        seam_neg="text, letters, signs, buildings, statue, perspective, blurry, smooth gradients",
        neg="perspective, vanishing point, blurry, smooth gradients, photo, 3d render, watermark, humans, people",
        pixel=4,
    ),
    "neon_harbour": dict(
        style="Seamless segment of an ultra-wide panorama of a cozy synthwave harbour city at night, neon magenta and cyan "
              "with warm orange windows, wet quays reflecting the lights, light rain haze, horizon line exactly at 62 "
              "percent of the image height, calm harbour water in the lower third, painterly digital illustration, soft "
              "bloom, extremely rich detail: tiny people with umbrellas, cats, scooters, shop signs, balconies with plants.",
        beats=[
            "A lighthouse on a stone pier, a cat silhouette sitting at its foot, fishermen with lanterns.",
            "Old brick warehouses along the quay, a cargo crane lifting a shipping container painted with the word "
            "\"BiBaBu\", dock workers and forklifts.",
            "A waterfront bar with a large glowing pink neon sign showing " + TEXT_REF + "; a crowd dancing on the "
            "terrace.",
            "A rooftop LAN party under string lights, many people at glowing computer monitors, a banner on the "
            "railing.",
            "A large white line-art mural of a beard on a brick wall, painted " + REF + "; a barber shop below it "
            "with bearded customers.",
            "A huge blimp drifting over the skyline with a glowing banner that reads \"BiBaBu\".",
            "A brewery with copper kettles behind big windows and a giant glowing yellow neon sign of a foaming beer "
            "mug shaped " + REF + "; people with beer mugs on the quay.",
            "A canal with arched stone bridges, gondolas with lanterns and long neon reflections.",
            "A lingerie boutique with a glowing white neon sign of a bra outline, shaped " + REF + "; shoppers with "
            "bags under umbrellas.",
            "A ferry terminal, a ferry named \"BiBaBu\" with its lights on and a few moored boats.",
        ],
        logos={2: TEXT, 4: BART, 6: BIER, 8: BUSEN},
        seam="Calm harbour water, a quay, plain buildings with lit windows and soft city lights continuing across.",
        seam_neg="text, letters, neon signs, logos, lighthouse, crane, blimp, tower, boats, repeated objects",
    ),
    "mountains": dict(
        style="Seamless segment of an ultra-wide alpine panorama at dusk, layered mountain ridges fading into blue haze, "
              "warm peach and lavender sky, a calm lake in the foreground reflecting the sky, horizon line exactly at 60 "
              "percent of the image height, detailed matte painting, soft light, many small details along the shore: "
              "hikers, cows with bells, wooden fences, flowers, small boats.",
        beats=[
            "A wooden cabin with a lit window on the lake shore, smoke from the chimney, a dog on the porch.",
            "A pine forest and a waterfall falling from the ridge into the lake, a deer drinking at the water.",
            "A mountain hut with a wooden sign over the door, carved with " + TEXT_REF + "; hikers resting on benches.",
            "A small sailing boat on the still lake, its sail painted with the word \"BiBaBu\".",
            "A rocky mountain face whose snow-covered ridges form the beard outline, " + REF + ".",
            "The highest peak catching the last pink sunlight, a cable car climbing towards it.",
            "A beer garden on the meadow with long tables, lanterns and a big wooden sign of a foaming beer mug, "
            "shaped " + REF + ".",
            "Hot-air balloons over the far ridges, one of them striped and carrying a \"BiBaBu\" banner.",
            "Two round snowy hilltops side by side with a glowing sunset rim, shaped " + REF + ".",
            "A small chapel on a hill above alpine meadows with flowers and grazing cows.",
        ],
        logos={2: TEXT, 4: BART, 6: BIER, 8: BUSEN},
        seam="Mountain ridges, blue haze, sky, shore and the calm lake continuing across.",
        seam_neg="text, letters, signs, logos, buildings, cabin, hut, boats, balloons, repeated objects",
    ),
    "fantasy_realm": dict(
        style="Seamless segment of an ultra-wide high-fantasy landscape panorama at golden hour, a wide river valley with "
              "distant snowy peaks and floating islands, warm golden light and soft mist, horizon line exactly at 60 percent "
              "of the image height, epic painterly concept art, extremely detailed: tiny travellers, carts, banners, "
              "birds, glowing windows.",
        beats=[
            "A white castle with tall spires on a cliff, banners with a golden beer mug emblem waving.",
            "A floating island with a waterfall pouring into the clouds below, griffins circling it.",
            "The great stone gate of a dwarven city in the mountainside, " + TEXT_REF + " carved in gold above it, "
            "dwarves marching in.",
            "A green dragon flying over a forest, a knight on horseback watching from a hill.",
            "A colossal dwarf statue carved into the mountain, its stone beard shaped " + REF + ".",
            "An elven forest with giant glowing trees, hanging lanterns and rope bridges.",
            "A cozy fantasy tavern with a hanging wooden sign of a foaming beer mug, shaped " + REF + "; adventurers "
            "feasting at tables outside.",
            "A wizard tower with a glowing crystal on top, the runes \"BiBaBu\" glowing on its wall.",
            "Two round glowing crystal orbs side by side on a stone altar, their golden rims shaped " + REF + ".",
            "A village with thatched roofs by the river, a market fair with tents and a maypole.",
        ],
        logos={2: TEXT, 4: BART, 6: BIER, 8: BUSEN},
        seam="The river valley, meadows, distant peaks and sky continuing across.",
        seam_neg="text, letters, signs, castle, tower, statue, dragon, buildings, repeated objects",
    ),
}
