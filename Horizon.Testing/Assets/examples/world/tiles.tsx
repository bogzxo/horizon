<?xml version="1.0" encoding="UTF-8"?>
<tileset version="1.10" tiledversion="1.11.0" name="tiles" tilewidth="16" tileheight="16" tilecount="32" columns="8">
 <image source="tiles_albedo.png" width="128" height="64"/>
 <tile id="16">
  <animation>
   <frame tileid="16" duration="140"/>
   <frame tileid="17" duration="180"/>
  </animation>
 </tile>
 <tile id="21">
  <objectgroup draworder="index" id="2">
   <object id="1" x="0" y="8" width="16" height="8"/>
  </objectgroup>
 </tile>
 <tile id="19" type="sign">
  <properties>
   <property name="label" value="mind the step"/>
  </properties>
 </tile>
</tileset>
